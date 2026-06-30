using System.Collections.Generic;
using System.Linq;
using ReportService.DeepLinks;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Unit coverage of <see cref="RSCDeepLinkQuery"/>: the cap/clamp/dedupe normalisation and the
/// fragment/encoding-aware URL append used to forward attribution params onto the redirect.
/// </summary>
public class DeepLinkQueryTests
{
    private static IReadOnlyDictionary<string, string> Normalize(
        IEnumerable<KeyValuePair<string, string?>> raw, int maxParams = 16, int maxLen = 256)
        => RSCDeepLinkQuery.Normalize(raw, maxParams, maxLen);

    private static KeyValuePair<string, string?> P(string k, string? v) => new(k, v);

    [Fact]
    public void Normalize_caps_param_count()
    {
        var raw = Enumerable.Range(0, 30).Select(i => P($"p{i}", i.ToString()));
        var result = Normalize(raw, maxParams: 5);
        Assert.Equal(5, result.Count);
        Assert.True(result.ContainsKey("p0"));
        Assert.False(result.ContainsKey("p5")); // 6th onwards dropped
    }

    [Fact]
    public void Normalize_clamps_key_and_value_length()
    {
        var result = Normalize(new[] { P(new string('k', 50), new string('v', 50)) }, maxLen: 10);
        var only = result.Single();
        Assert.Equal(10, only.Key.Length);
        Assert.Equal(10, only.Value.Length);
    }

    [Fact]
    public void Normalize_keeps_first_value_and_skips_blank_keys()
    {
        var result = Normalize(new[] { P("a", "1"), P("a", "2"), P("  ", "x"), P("b", null) });
        Assert.Equal(2, result.Count);
        Assert.Equal("1", result["a"]);     // first value wins
        Assert.Equal("", result["b"]);      // null value becomes empty string
        Assert.DoesNotContain(result.Keys, k => string.IsNullOrWhiteSpace(k));
    }

    [Fact]
    public void Append_adds_query_to_base_without_one()
    {
        var url = RSCDeepLinkQuery.Append("myapp://promo/spring", new Dictionary<string, string> { ["utm"] = "fb" });
        Assert.Equal("myapp://promo/spring?utm=fb", url);
    }

    [Fact]
    public void Append_uses_ampersand_when_base_already_has_query()
    {
        var url = RSCDeepLinkQuery.Append("https://x/p?z=9", new Dictionary<string, string> { ["a"] = "1" });
        Assert.Equal("https://x/p?z=9&a=1", url);
    }

    [Fact]
    public void Append_inserts_params_before_a_fragment()
    {
        var url = RSCDeepLinkQuery.Append("https://x/p#frag", new Dictionary<string, string> { ["a"] = "1" });
        Assert.Equal("https://x/p?a=1#frag", url);
    }

    [Fact]
    public void Append_percent_encodes_keys_and_values()
    {
        var url = RSCDeepLinkQuery.Append("https://x/p", new Dictionary<string, string> { ["a b"] = "c&d=e" });
        Assert.Equal("https://x/p?a%20b=c%26d%3De", url);
    }

    [Fact]
    public void Append_returns_base_unchanged_when_no_params()
    {
        Assert.Equal("myapp://x", RSCDeepLinkQuery.Append("myapp://x", null));
        Assert.Equal("myapp://x", RSCDeepLinkQuery.Append("myapp://x", new Dictionary<string, string>()));
    }

    [Fact]
    public void Serialize_roundtrips_through_deserialize()
    {
        var map = new Dictionary<string, string> { ["utm_source"] = "fb", ["promo"] = "ABC" };
        var json = RSCDeepLinkQuery.Serialize(map);
        Assert.NotNull(json);
        var back = RSCDeepLinkQuery.Deserialize(json);
        Assert.Equal("fb", back!["utm_source"]);
        Assert.Equal("ABC", back["promo"]);

        Assert.Null(RSCDeepLinkQuery.Serialize(new Dictionary<string, string>())); // empty -> null
        Assert.Null(RSCDeepLinkQuery.Deserialize(null));
        Assert.Null(RSCDeepLinkQuery.Deserialize("{not json"));
    }
}
