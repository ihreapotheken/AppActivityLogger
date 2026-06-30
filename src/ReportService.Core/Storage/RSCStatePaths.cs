using ReportService.Storage.Catalog;

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

    /// <summary>
    /// Resolves a per-app data file under <c>{ReportsRoot}/apps/{client}/{app}/{fileName}</c> — the
    /// database-per-app layout where each <c>(client, app)</c> entry owns its own analytics +
    /// problem-report databases. Client/app slugs are normalized and are already constrained to
    /// <c>^[a-z0-9][a-z0-9-]{0,63}$</c> by <see cref="RSCCatalogSlug"/>, so they are safe single path
    /// segments (no traversal). Always anchored under the writable reports volume.
    /// </summary>
    public static string ResolveAppDb(string reportsRoot, string clientSlug, string appSlug, string fileName)
    {
        var client = RSCCatalogSlug.Normalize(clientSlug);
        var app = RSCCatalogSlug.Normalize(appSlug);
        if (client.Length == 0) client = "default";
        if (app.Length == 0) app = "default";
        return Path.Combine(reportsRoot, "apps", client, app, fileName);
    }
}
