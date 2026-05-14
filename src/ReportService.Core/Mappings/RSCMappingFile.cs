using System.Collections.Generic;

namespace ReportService.Mappings;

/// <summary>
/// Parsed in-memory representation of a single R8 / ProGuard <c>mapping.txt</c>. Indexed by
/// obfuscated class name for O(1) lookups during deobfuscation. Methods within a class are
/// kept ordered so <see cref="RSCMappingApplier"/> can pick the right inlined frame using the
/// `:line` ranges encoded in the mapping file.
/// </summary>
public sealed class RSCMappingFile
{
    public IReadOnlyDictionary<string, RSCMappedClass> ClassesByObfuscated { get; }

    public RSCMappingFile(IReadOnlyDictionary<string, RSCMappedClass> classesByObfuscated)
    {
        ClassesByObfuscated = classesByObfuscated;
    }
}

/// <summary>One class entry in the mapping. <see cref="Methods"/> is in source order so the
/// applier can match on `(obfuscatedName, lineNumber)` against the encoded line ranges.
/// <see cref="SourceFile"/> is lifted from the per-class `# {"id":"sourceFile","fileName":"…"}`
/// metadata that R8 emits — it's the canonical source filename to fall back to when a frame's
/// source token is opaque (e.g. `r8-map-id-…`, `SourceFile`, `Unknown Source`).</summary>
public sealed class RSCMappedClass
{
    public string OriginalName { get; }
    public string ObfuscatedName { get; }
    public IReadOnlyList<RSCMappedMethod> Methods { get; }
    public IReadOnlyDictionary<string, string> FieldsByObfuscated { get; }
    public string? SourceFile { get; }

    public RSCMappedClass(
        string originalName,
        string obfuscatedName,
        IReadOnlyList<RSCMappedMethod> methods,
        IReadOnlyDictionary<string, string> fieldsByObfuscated,
        string? sourceFile = null)
    {
        OriginalName = originalName;
        ObfuscatedName = obfuscatedName;
        Methods = methods;
        FieldsByObfuscated = fieldsByObfuscated;
        SourceFile = sourceFile;
    }
}

/// <summary>
/// One method entry. R8 emits inlined methods as additional rows that share the obfuscated
/// name but differ in <c>StartLine</c> / <c>EndLine</c> (the obfuscated method's PC range)
/// and <c>OriginalStartLine</c> (the original source line). When a stack frame's line falls
/// inside `[StartLine, EndLine]` the applier rewrites to the original method + line.
/// </summary>
public sealed class RSCMappedMethod
{
    public string OriginalName { get; }
    public string ObfuscatedName { get; }
    public string ReturnType { get; }
    public string ParameterList { get; }
    /// <summary>0 when the mapping row didn't include a line range — applies to any frame.</summary>
    public int StartLine { get; }
    public int EndLine { get; }
    public int OriginalStartLine { get; }
    public int OriginalEndLine { get; }
    /// <summary>Optional outer class for inlined methods (R8 emits a fully-qualified name when
    /// the inlined method came from a different class).</summary>
    public string? OriginalClass { get; }

    public RSCMappedMethod(
        string originalName,
        string obfuscatedName,
        string returnType,
        string parameterList,
        int startLine,
        int endLine,
        int originalStartLine,
        int originalEndLine,
        string? originalClass = null)
    {
        OriginalName = originalName;
        ObfuscatedName = obfuscatedName;
        ReturnType = returnType;
        ParameterList = parameterList;
        StartLine = startLine;
        EndLine = endLine;
        OriginalStartLine = originalStartLine;
        OriginalEndLine = originalEndLine;
        OriginalClass = originalClass;
    }
}
