namespace ReportService.Storage;

/// <summary>
/// Resolves SQLite / other state file paths so they land inside the writable <c>ReportsRoot</c> by
/// default. Absolute paths are honored verbatim; relative paths are anchored under the reports root.
/// This is what lets the service run under a read-only content root (Docker <c>read_only: true</c>,
/// systemd <c>ProtectSystem=strict</c>) without operators having to remember to override every DB
/// path explicitly.
/// </summary>
public static class RSCStatePaths
{
    public static string Resolve(string? path, string reportsRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.IsPathRooted(path) ? path : Path.Combine(reportsRoot, path);
    }
}
