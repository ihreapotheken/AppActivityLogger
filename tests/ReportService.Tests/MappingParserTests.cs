using ReportService.Mappings;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Coverage for <see cref="RSCMappingParser"/> + <see cref="RSCMappingApplier"/>. Inputs use
/// real R8/ProGuard mapping shapes pulled from production builds (whitespace + the leading
/// `# {compiler}` metadata block trimmed for brevity).
/// </summary>
public class MappingParserTests
{
    [Fact]
    public void Parses_class_and_method_basic()
    {
        const string mapping = """
            de.example.LegalNoticeKt -> a.b.c:
                void <init>() -> <init>
                void LegalNotice(kotlin.jvm.functions.Function0) -> a
                int $stable -> b
            """;
        var parser = new RSCMappingParser();
        var parsed = parser.Parse(new StringReader(mapping));

        Assert.True(parsed.ClassesByObfuscated.TryGetValue("a.b.c", out var cls));
        Assert.NotNull(cls);
        Assert.Equal("de.example.LegalNoticeKt", cls!.OriginalName);
        Assert.Contains(cls.Methods, m => m.OriginalName == "LegalNotice" && m.ObfuscatedName == "a");
        Assert.Equal("$stable", cls.FieldsByObfuscated["b"]);
        Assert.Equal(0, parser.SkippedLines);
    }

    [Fact]
    public void Applier_rewrites_obfuscated_frame_to_original_symbols()
    {
        const string mapping = """
            de.example.LegalNoticeKt -> a.b.c:
                1:5:void LegalNotice$lambda$1$lambda$0():27:31 -> a
            """;
        var parsed = new RSCMappingParser().Parse(new StringReader(mapping));
        var trace = "java.lang.RuntimeException: simulated\n\tat a.b.c.a(SourceFile:3)";
        var applier = new RSCMappingApplier(parsed);
        var result = applier.Apply(trace);

        Assert.Contains("at de.example.LegalNoticeKt.LegalNotice$lambda$1$lambda$0(LegalNoticeKt.kt:29)", result);
        Assert.Equal(1, applier.RewrittenFrames);
    }

    [Fact]
    public void Applier_picks_inlined_method_by_line_range()
    {
        // Two methods share the obfuscated name but cover different obfuscated PC ranges.
        const string mapping = """
            de.example.Outer -> a.b:
                1:10:void outer():100:109 -> a
                11:15:void Inner.inlined():200:204 -> a
            """;
        var parsed = new RSCMappingParser().Parse(new StringReader(mapping));
        var applier = new RSCMappingApplier(parsed);

        var hitsOuter = applier.Apply("\tat a.b.a(SourceFile:5)");
        var hitsInlined = applier.Apply("\tat a.b.a(SourceFile:13)");

        Assert.Contains("at de.example.Outer.outer(", hitsOuter);
        Assert.Contains("Outer.kt:104", hitsOuter);
        Assert.Contains("at Inner.inlined(", hitsInlined);
        Assert.Contains(":202", hitsInlined);
    }

    [Fact]
    public void Applier_leaves_unknown_classes_untouched()
    {
        var parsed = new RSCMappingParser().Parse(new StringReader("de.example.Foo -> a.b:\n    void bar() -> a\n"));
        var applier = new RSCMappingApplier(parsed);
        var trace = "\tat com.example.Other.x(SourceFile:5)";
        Assert.Equal(trace, applier.Apply(trace));
        Assert.Equal(0, applier.RewrittenFrames);
    }

    [Fact]
    public void Parses_real_world_layout_with_metadata_comments()
    {
        const string mapping = """
            # compiler: R8
            # compiler_version: 8.7.18
            # min_api: 30
            de.ihreapotheken.sdk.core.ui.composables.footer.LegalNoticeKt -> de.ihreapotheken.sdk.core.ui.composables.footer.LegalNoticeKt:
                # {"id":"sourceFile","fileName":"LegalNotice.kt"}
                1:1:void LegalNotice$lambda$1$lambda$0():27:27 -> a
                1:1:void $r8$lambda$kOOXZoJAIIPpbp35V0NjYfjXJww():0:0 -> b
            """;
        var parsed = new RSCMappingParser().Parse(new StringReader(mapping));

        Assert.Single(parsed.ClassesByObfuscated);
        Assert.True(parsed.ClassesByObfuscated.TryGetValue(
            "de.ihreapotheken.sdk.core.ui.composables.footer.LegalNoticeKt", out var cls));
        Assert.Equal(2, cls!.Methods.Count);
    }

    [Fact]
    public void Applier_substitutes_opaque_r8_source_token_with_mapping_source_file()
    {
        // R8 emits `r8-map-id-<hex>` for outlined/collapsed code where it can't pin a real
        // source file in the trace itself. The class block's sourceFile metadata is what the
        // applier should fall back to.
        const string mapping = """
            de.example.ProductGridViewModel -> de.example.ProductGridViewModel:
            # {"id":"sourceFile","fileName":"ProductGridViewModel.kt"}
                6:13:void onProductClicked(de.example.Product):208:208 -> onProductClicked
            """;
        var parsed = new RSCMappingParser().Parse(new StringReader(mapping));
        var applier = new RSCMappingApplier(parsed);
        var trace = "\tat de.example.ProductGridViewModel.onProductClicked(r8-map-id-deadbeefcafe:10)";
        var result = applier.Apply(trace);

        Assert.Contains(
            "at de.example.ProductGridViewModel.onProductClicked(ProductGridViewModel.kt:208)",
            result);
        Assert.DoesNotContain("r8-map-id", result);
    }

    [Fact]
    public void Applier_rewrites_exception_header_when_obfuscated()
    {
        const string mapping = """
            kotlin.RuntimeException -> a.b.c:
            """;
        var parsed = new RSCMappingParser().Parse(new StringReader(mapping));
        var applier = new RSCMappingApplier(parsed);
        var trace = "a.b.c: simulated message";
        Assert.StartsWith("kotlin.RuntimeException:", applier.Apply(trace));
    }

    [Fact]
    public void Chain_applies_two_mappings_in_order_to_double_obfuscated_frame()
    {
        // SDK + host both obfuscated:
        //   1. Original SDK code:   de.example.Foo.bar
        //   2. SDK consumer R8:     de.example.x.y.z.Foo.a
        //   3. Host R8 on top:      q.r.s.b
        // Frame in trace: `at q.r.s.b(SourceFile:1)`.
        // Expected: host mapping reverses to `de.example.x.y.z.Foo.a`, SDK consumer mapping
        // reverses on top to `de.example.Foo.bar`.
        const string hostMapping = """
            de.example.x.y.z.Foo -> q.r.s:
                void a() -> b
            """;
        const string sdkConsumerMapping = """
            de.example.Foo -> de.example.x.y.z.Foo:
            # {"id":"sourceFile","fileName":"Foo.kt"}
                void bar() -> a
            """;

        var parser = new RSCMappingParser();
        var chain = new[]
        {
            parser.Parse(new StringReader(hostMapping)),
            parser.Parse(new StringReader(sdkConsumerMapping)),
        };

        var applier = new RSCMappingChainApplier(chain);
        var result = applier.Apply("\tat q.r.s.b(SourceFile:1)");

        Assert.Contains("at de.example.Foo.bar(", result);
        Assert.Equal(2, applier.RewrittenFrames);
    }
}
