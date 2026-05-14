using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportService.Mappings;

/// <summary>
/// Rewrites obfuscated stack-trace text using a parsed <see cref="RSCMappingFile"/>. Operates
/// purely on text: scans each line for a frame in the canonical Java/Kotlin <c>at &lt;class&gt;.&lt;method&gt;(&lt;file&gt;:&lt;line&gt;)</c>
/// shape, looks up the obfuscated class, picks the method whose encoded line range covers the
/// frame's line, and writes back the original symbols. Lines that don't match the frame regex
/// (the exception header, message body, "..." continuations) pass through untouched. The first
/// header line "<c>className: message</c>" is also rewritten when the leading token is a known
/// obfuscated class.
/// </summary>
public sealed class RSCMappingApplier
{
    // `\s+at <class>.<method>(<file>:<line>)` — accepts `(SourceFile:NN)`, `(Unknown Source:0)`,
    // `(D8$$SyntheticClass:0)`, etc. The dotted left-hand side is greedy so the last segment is
    // always the method name; everything before the final dot is the class.
    private static readonly Regex FrameRegex = new(
        @"^(?<indent>\s*)at\s+(?<class>[^\s(]+)\.(?<method>[^\s(.]+)\((?<file>[^:)]*):?(?<line>\d*)\)(?<tail>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Standalone class.method form without `at` (e.g. R8's `D8$$SyntheticClass.invoke`). Less
    // common in raw traces but appears in tombstone summaries.
    private static readonly Regex HeaderRegex = new(
        @"^(?<class>[A-Za-z_$][\w$.]*?)(?<rest>:.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly RSCMappingFile _mapping;

    public RSCMappingApplier(RSCMappingFile mapping)
    {
        _mapping = mapping;
    }

    /// <summary>Total number of frame lines the applier successfully rewrote — surfaced in the
    /// admin UI so an operator can tell at a glance whether the uploaded mapping covers the
    /// trace.</summary>
    public int RewrittenFrames { get; private set; }

    public string Apply(string trace)
    {
        if (string.IsNullOrEmpty(trace)) return trace;
        var sb = new StringBuilder(trace.Length);
        var lines = trace.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.EndsWith('\r') ? raw.Substring(0, raw.Length - 1) : raw;
            var carry = raw.EndsWith('\r') ? "\r" : string.Empty;

            string rewritten;
            var frameMatch = FrameRegex.Match(line);
            if (frameMatch.Success)
            {
                rewritten = RewriteFrame(line, frameMatch);
            }
            else if (i == 0)
            {
                // First line is conventionally `<exceptionClass>: <message>` — try to rewrite.
                rewritten = RewriteHeader(line);
            }
            else
            {
                rewritten = line;
            }

            sb.Append(rewritten);
            sb.Append(carry);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private string RewriteFrame(string original, Match m)
    {
        var obfuscatedClass = m.Groups["class"].Value;
        var obfuscatedMethod = m.Groups["method"].Value;
        var fileName = m.Groups["file"].Value;
        var lineText = m.Groups["line"].Value;

        if (!_mapping.ClassesByObfuscated.TryGetValue(obfuscatedClass, out var mappedClass))
        {
            // Try inner-class folding: `a.b.c$d` falls back to `a.b.c` when the inner class
            // wasn't separately renamed (R8 sometimes keeps inner-class structure intact).
            return original;
        }

        var line = 0;
        if (lineText.Length > 0 && int.TryParse(lineText, out var parsed)) line = parsed;

        var (originalMethod, originalLine, originalClassOverride) = ResolveMethod(mappedClass, obfuscatedMethod, line);

        var className = originalClassOverride ?? mappedClass.OriginalName;
        var lineLabel = originalLine > 0 ? originalLine.ToString() : (lineText.Length > 0 ? lineText : "0");
        var fileLabel = ChooseFileName(fileName, className, mappedClass.SourceFile);

        RewrittenFrames++;
        return $"{m.Groups["indent"].Value}at {className}.{originalMethod}({fileLabel}:{lineLabel}){m.Groups["tail"].Value}";
    }

    private string RewriteHeader(string line)
    {
        var m = HeaderRegex.Match(line);
        if (!m.Success) return line;
        var head = m.Groups["class"].Value;
        if (!_mapping.ClassesByObfuscated.TryGetValue(head, out var mappedClass)) return line;
        return mappedClass.OriginalName + (m.Groups["rest"].Success ? m.Groups["rest"].Value : string.Empty);
    }

    private (string Method, int OriginalLine, string? OriginalClassOverride) ResolveMethod(
        RSCMappedClass cls, string obfuscatedName, int frameLine)
    {
        // First pass: methods whose `[startLine, endLine]` cover the frame's line. Inlined
        // methods are emitted by R8 with the obfuscated method name shared with the outer
        // method, so a line-range match is the only way to disambiguate them.
        RSCMappedMethod? rangeHit = null;
        RSCMappedMethod? nameHit = null;
        foreach (var method in cls.Methods)
        {
            if (!string.Equals(method.ObfuscatedName, obfuscatedName, StringComparison.Ordinal)) continue;
            nameHit ??= method;
            if (method.StartLine == 0 && method.EndLine == 0) continue;
            if (frameLine >= method.StartLine && frameLine <= method.EndLine)
            {
                rangeHit = method;
                break;
            }
        }

        var pick = rangeHit ?? nameHit;
        if (pick is null) return (obfuscatedName, frameLine, null);

        var originalLine = ComputeOriginalLine(pick, frameLine);
        return (pick.OriginalName, originalLine, pick.OriginalClass);
    }

    private static int ComputeOriginalLine(RSCMappedMethod method, int frameLine)
    {
        // R8 encodes a 1:1 mapping when the obfuscated and original ranges have the same
        // length. When the range collapsed during inlining (e.g. `5:5:foo():12:12`), every
        // obfuscated line maps back to the single original line.
        if (frameLine == 0) return method.OriginalStartLine;
        if (method.StartLine == 0) return method.OriginalStartLine;

        var span = method.EndLine - method.StartLine;
        var originalSpan = method.OriginalEndLine - method.OriginalStartLine;
        if (span <= 0 || originalSpan <= 0) return method.OriginalStartLine;
        // Linear interpolation; fine for inlined-or-otherwise spans because R8 emits proportional ranges.
        var offset = frameLine - method.StartLine;
        var scaled = originalSpan == span
            ? offset
            : (int)Math.Round(offset * (originalSpan / (double)span));
        return method.OriginalStartLine + scaled;
    }

    private static string ChooseFileName(string fileFromTrace, string originalClassName, string? mappingSourceFile)
    {
        // R8 produces a handful of opaque source tokens that we want to substitute with the
        // real filename. Order of preference:
        //   1. The per-class `# {"id":"sourceFile","fileName":"…"}` metadata when available —
        //      authoritative because the mapping recorded the actual `.kt`/`.java` source.
        //   2. The trace's source token when it looks meaningful (e.g. `LegalNotice.kt`).
        //   3. A name synthesised from the original class — last-ditch fallback so frames at
        //      least point at a plausible IDE file.
        if (IsOpaqueSourceToken(fileFromTrace))
        {
            if (!string.IsNullOrEmpty(mappingSourceFile)) return mappingSourceFile!;

            var lastDot = originalClassName.LastIndexOf('.');
            var simple = lastDot >= 0 ? originalClassName.Substring(lastDot + 1) : originalClassName;
            // Strip generated suffixes: `LegalNoticeKt$LegalNotice$1$1` -> `LegalNoticeKt`.
            var dollar = simple.IndexOf('$');
            if (dollar > 0) simple = simple.Substring(0, dollar);
            return simple + ".kt";
        }
        return fileFromTrace;
    }

    /// <summary>
    /// True when the source-file token in a stack frame doesn't actually point at a real source
    /// file — e.g. R8 emits <c>r8-map-id-&lt;hex&gt;</c> for collapsed/outlined code, or the
    /// classic <c>SourceFile</c> / <c>Unknown Source</c> placeholders. In those cases the
    /// mapping's per-class <c>sourceFile</c> metadata is what we want to render instead.
    /// </summary>
    private static bool IsOpaqueSourceToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        if (string.Equals(token, "SourceFile", StringComparison.Ordinal)) return true;
        if (string.Equals(token, "Unknown Source", StringComparison.Ordinal)) return true;
        if (token.StartsWith("r8-map-id-", StringComparison.Ordinal)) return true;
        if (token.StartsWith("R8$$SyntheticClass", StringComparison.Ordinal)) return true;
        if (token.StartsWith("D8$$SyntheticClass", StringComparison.Ordinal)) return true;
        return false;
    }
}
