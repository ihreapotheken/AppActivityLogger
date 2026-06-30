using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Storage;

/// <summary>
/// Resolves the problem-report store for a given <c>(client, app)</c> in the database-per-app model —
/// each app owns its own file tree at <c>{ReportsRoot}/apps/{client}/{app}/{platform}/problem-reports/</c>.
/// Cached per <c>(client, app)</c>; the directory tree is created on first use.
/// </summary>
public interface RSCIReportStoreFactory
{
    /// <summary>The report store for one app, creating its directory tree on first use. Null/blank
    /// slugs coalesce to the configured default client/app.</summary>
    RSCIReportStore Get(string? clientSlug, string? appSlug);
}

public sealed class RSCReportStoreFactory : RSCIReportStoreFactory
{
    private readonly string _reportsRoot;
    private readonly RSCReportServiceOptions _options;
    private readonly string _defaultClient;
    private readonly string _defaultApp;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<(string Client, string App), Lazy<RSCIReportStore>> _stores = new();

    public RSCReportStoreFactory(
        RSCReportServiceOptions options,
        RSCCatalogOptions catalogOptions,
        ILoggerFactory loggerFactory)
    {
        _reportsRoot = options.ReportsRoot;
        _options = options;
        _loggerFactory = loggerFactory;
        _defaultClient = RSCCatalogSlug.Normalize(string.IsNullOrWhiteSpace(catalogOptions.DefaultClientSlug) ? "default" : catalogOptions.DefaultClientSlug);
        _defaultApp = RSCCatalogSlug.Normalize(string.IsNullOrWhiteSpace(catalogOptions.DefaultAppSlug) ? "default" : catalogOptions.DefaultAppSlug);
    }

    public RSCIReportStore Get(string? clientSlug, string? appSlug)
    {
        var client = Coalesce(clientSlug, _defaultClient);
        var app = Coalesce(appSlug, _defaultApp);
        return _stores.GetOrAdd((client, app), key => new Lazy<RSCIReportStore>(() =>
        {
            // The app's own report tree + its own SQLite index, so the index acceleration + the
            // crash-stack/log-summary extraction (which the indexing decorator does on Save) work
            // per app — not just in the global SqliteIndex deployment.
            var appRoot = Path.Combine(_reportsRoot, "apps", key.Client, key.App);
            var fileStore = new RSCFileSystemReportStore(appRoot, _options, _loggerFactory.CreateLogger<RSCFileSystemReportStore>());
            var index = new RSCSqliteReportIndex(
                Path.Combine(appRoot, "reports.db"), Path.Combine(appRoot, "backups"),
                _options, _loggerFactory.CreateLogger<RSCSqliteReportIndex>());
            return new RSCSqliteIndexingReportStore(fileStore, index, _loggerFactory.CreateLogger<RSCSqliteIndexingReportStore>());
        })).Value;
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : RSCCatalogSlug.Normalize(value);
}
