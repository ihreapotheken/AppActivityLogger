using System;
using System.Collections.Generic;
using System.IO;

namespace ReportService.Mappings;

/// <summary>
/// Parses an R8 / ProGuard <c>mapping.txt</c> stream into an indexed <see cref="RSCMappingFile"/>.
///
/// Format (whitespace + comments stripped):
/// <code>
/// originalClass -> obfuscatedClass:
///     returnType methodName(paramTypes) -> obfuscatedMethod
///     startLine:endLine:returnType methodName(paramTypes) -> obfuscatedMethod
///     startLine:endLine:returnType originalClass.methodName(paramTypes):origStart:origEnd -> obfuscatedMethod
/// </code>
///
/// Field lines mirror the simple method shape minus the parens. Lines beginning with <c>#</c>
/// are R8 metadata (compiler version, common-typo flags) — preserved as comments only. The
/// parser is tolerant: a malformed line is skipped with a remembered count so callers can
/// surface the figure to operators without aborting an upload.
/// </summary>
public sealed class RSCMappingParser
{
    public int SkippedLines { get; private set; }

    public RSCMappingFile Parse(Stream input)
    {
        using var reader = new StreamReader(input, leaveOpen: true);
        return Parse(reader);
    }

    public RSCMappingFile Parse(TextReader reader)
    {
        var classes = new Dictionary<string, RSCMappedClass>(StringComparer.Ordinal);
        string? currentOriginal = null;
        string? currentObfuscated = null;
        string? currentSourceFile = null;
        var methods = new List<RSCMappedMethod>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        void Flush()
        {
            if (currentOriginal is null || currentObfuscated is null) return;
            classes[currentObfuscated] = new RSCMappedClass(
                currentOriginal, currentObfuscated, methods.ToArray(), fields, currentSourceFile);
            methods = new List<RSCMappedMethod>();
            fields = new Dictionary<string, string>(StringComparer.Ordinal);
            currentOriginal = null;
            currentObfuscated = null;
            currentSourceFile = null;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            // Comments at the file root carry global metadata (compiler version, pg_map_id);
            // comments inside a class block carry per-class metadata, most importantly
            // `# {"id":"sourceFile","fileName":"Foo.kt"}` which we lift into the class entry.
            if (line.TrimStart().StartsWith("#"))
            {
                if (currentObfuscated is not null && currentSourceFile is null)
                {
                    var sf = TryExtractSourceFile(line);
                    if (sf is not null) currentSourceFile = sf;
                }
                continue;
            }

            var isMember = line[0] == ' ' || line[0] == '\t';
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (!isMember)
            {
                Flush();
                if (!TryParseClassHeader(trimmed, out var orig, out var obfu))
                {
                    SkippedLines++;
                    continue;
                }
                currentOriginal = orig;
                currentObfuscated = obfu;
                continue;
            }

            if (currentObfuscated is null)
            {
                // Member line without an enclosing class — malformed file, skip.
                SkippedLines++;
                continue;
            }

            if (TryParseMember(trimmed, out var method, out var fieldOriginal, out var fieldObfuscated))
            {
                if (method is not null)
                {
                    methods.Add(method);
                }
                else if (fieldOriginal is not null && fieldObfuscated is not null)
                {
                    fields[fieldObfuscated] = fieldOriginal;
                }
            }
            else
            {
                SkippedLines++;
            }
        }
        Flush();

        return new RSCMappingFile(classes);
    }

    /// <summary><c>de.example.Foo -> a.b.c:</c></summary>
    private static bool TryParseClassHeader(string line, out string original, out string obfuscated)
    {
        original = string.Empty;
        obfuscated = string.Empty;

        if (!line.EndsWith(':')) return false;
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow < 0) return false;

        original = line.Substring(0, arrow).Trim();
        obfuscated = line.Substring(arrow + 4, line.Length - arrow - 5).Trim();
        return original.Length > 0 && obfuscated.Length > 0;
    }

    /// <summary>
    /// A member line — either a method or a field. The applier only needs methods + fields, so
    /// we parse both into the appropriate slot. Returns false on malformed input.
    /// </summary>
    private static bool TryParseMember(
        string line,
        out RSCMappedMethod? method,
        out string? fieldOriginal,
        out string? fieldObfuscated)
    {
        method = null;
        fieldOriginal = null;
        fieldObfuscated = null;

        var arrow = line.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (arrow < 0) return false;

        var left = line.Substring(0, arrow);
        var obfuscated = line.Substring(arrow + 4).Trim();
        if (obfuscated.Length == 0) return false;

        // Optional leading line range: "startLine:endLine:rest"
        int startLine = 0, endLine = 0;
        var rest = left;
        var firstColon = left.IndexOf(':');
        if (firstColon > 0 && IsAsciiDigits(left, 0, firstColon))
        {
            var secondColon = left.IndexOf(':', firstColon + 1);
            if (secondColon > 0 && IsAsciiDigits(left, firstColon + 1, secondColon))
            {
                startLine = ParseInt(left, 0, firstColon);
                endLine = ParseInt(left, firstColon + 1, secondColon);
                rest = left.Substring(secondColon + 1);
            }
        }

        // Now `rest` is one of:
        //   "returnType methodName(paramList)"                        — base method
        //   "returnType methodName(paramList):origStart:origEnd"       — base + original lines
        //   "returnType originalClass.methodName(paramList)[:o1:o2]"   — inlined frame
        //   "fieldType fieldName"                                      — field
        var spaceIdx = rest.IndexOf(' ');
        if (spaceIdx <= 0) return false;
        var returnType = rest.Substring(0, spaceIdx);
        var afterReturn = rest.Substring(spaceIdx + 1).Trim();

        var parenIdx = afterReturn.IndexOf('(');
        if (parenIdx < 0)
        {
            // Field — `fieldType fieldName`.
            fieldOriginal = afterReturn.Trim();
            fieldObfuscated = obfuscated;
            return fieldOriginal.Length > 0;
        }

        var closingParen = afterReturn.IndexOf(')', parenIdx);
        if (closingParen < 0) return false;

        var qualifiedName = afterReturn.Substring(0, parenIdx);
        var paramList = afterReturn.Substring(parenIdx + 1, closingParen - parenIdx - 1);

        // qualifiedName may be `methodName` or `originalClass.methodName`.
        string? originalClass = null;
        var lastDot = qualifiedName.LastIndexOf('.');
        var methodName = lastDot >= 0 ? qualifiedName.Substring(lastDot + 1) : qualifiedName;
        if (lastDot >= 0) originalClass = qualifiedName.Substring(0, lastDot);

        // Trailing `:origStart:origEnd` after the close-paren.
        int origStart = startLine, origEnd = endLine;
        var trailing = afterReturn.Substring(closingParen + 1);
        if (trailing.Length > 0 && trailing[0] == ':')
        {
            var parts = trailing.Substring(1).Split(':');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var os)) origStart = os;
            if (parts.Length >= 2 && int.TryParse(parts[1], out var oe)) origEnd = oe;
            else if (parts.Length == 1) origEnd = origStart;
        }

        method = new RSCMappedMethod(
            originalName: methodName,
            obfuscatedName: obfuscated,
            returnType: returnType,
            parameterList: paramList,
            startLine: startLine,
            endLine: endLine,
            originalStartLine: origStart,
            originalEndLine: origEnd,
            originalClass: originalClass);
        return true;
    }

    /// <summary>
    /// Extracts the <c>fileName</c> value from R8's per-class JSON metadata comment, e.g.
    /// <c># {"id":"sourceFile","fileName":"LegalNotice.kt"}</c>. Naïve substring search — we
    /// don't pull in a JSON parser for this single metadata key, and the value is always a
    /// quoted ASCII filename in practice.
    /// </summary>
    internal static string? TryExtractSourceFile(string commentLine)
    {
        const string idMarker = "\"id\":\"sourceFile\"";
        const string nameKey = "\"fileName\":\"";
        var idAt = commentLine.IndexOf(idMarker, StringComparison.Ordinal);
        if (idAt < 0) return null;
        var nameAt = commentLine.IndexOf(nameKey, idAt, StringComparison.Ordinal);
        if (nameAt < 0) return null;
        var start = nameAt + nameKey.Length;
        var end = commentLine.IndexOf('"', start);
        if (end < 0 || end == start) return null;
        return commentLine.Substring(start, end - start);
    }

    private static bool IsAsciiDigits(string s, int start, int end)
    {
        if (start >= end) return false;
        for (var i = start; i < end; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    private static int ParseInt(string s, int start, int end)
    {
        var n = 0;
        for (var i = start; i < end; i++) n = n * 10 + (s[i] - '0');
        return n;
    }
}
