namespace ReportService.Security;

/// <summary>Path-traversal guard: combine a trusted root with an untrusted leaf, refusing <c>..</c>, null bytes, invalid filename chars, and absolute paths.</summary>
public static class RSCSafePath
{
    public static bool TryCombine(string root, string relativeName, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativeName)) return false;
        if (relativeName.Contains('\0')) return false;
        if (relativeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (relativeName.Contains("..", StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(relativeName)) return false;

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativeName));

        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal)) return false;

        fullPath = candidate;
        return true;
    }
}
