using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Storage;

/// <summary>
/// Resolves the problem-report store for a given <c>(client, app)</c> in the database-per-app model —
/// each app owns its own file tree at <c>{ReportsRoot}/apps/{client}/{app}/{platform}/problem-reports/</c>
/// plus its own SQLite index at <c>{ReportsRoot}/apps/{client}/{app}/reports.db</c>. Both are cached per
/// <c>(client, app)</c>; the directory tree is created on first use.
/// </summary>
public interface RSCIReportStoreFactory
{
    /// <summary>The report store for one app, creating its directory tree on first use. Null/blank
    /// slugs coalesce to the configured default client/app.</summary>
    RSCIReportStore Get(string? clientSlug, string? appSlug);

    /// <summary>The maintenance/query surface (aggregate stats, search, summaries) of one app's own
    /// SQLite index — the per-app analogue of the global index. Callers fan this out across the
    /// catalog's apps + merge to produce cross-app aggregates (e.g. the Stats page).</summary>
    RSCIReportIndexMaintenance GetMaintenance(string? clientSlug, string? appSlug);

    /// <summary>Drop every cached store/index handle for a client's apps and release their pooled SQLite
    /// connections, so the on-disk report trees + indexes can be deleted. Used when a client is purged.</summary>
    void EvictClient(string clientSlug);

    /// <summary>Drop the cached store/index handle for one app and release its pooled SQLite
    /// connections, so its on-disk report tree + index can be deleted. Used when a single app is purged.</summary>
    void EvictApp(string clientSlug, string appSlug);
}

public sealed class RSCReportStoreFactory : RSCIReportStoreFactory
{
    private readonly string _reportsRoot;
    private readonly RSCReportServiceOptions _options;
    private readonly string _defaultClient;
    private readonly string _defaultApp;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<(string Client, string App), Lazy<RSCSqliteReportIndex>> _indexes = new();
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

    /// <summary>The app's own SQLite index, built + cached once per <c>(client, app)</c> and shared
    /// between the indexing store (writes) and the maintenance surface (reads).</summary>
    private RSCSqliteReportIndex IndexFor((string Client, string App) key) =>
        _indexes.GetOrAdd(key, k => new Lazy<RSCSqliteReportIndex>(() =>
        {
            var appRoot = Path.Combine(_reportsRoot, "apps", k.Client, k.App);
            return new RSCSqliteReportIndex(
                Path.Combine(appRoot, "reports.db"), Path.Combine(appRoot, "backups"),
                _options, _loggerFactory.CreateLogger<RSCSqliteReportIndex>());
        })).Value;

    public RSCIReportStore Get(string? clientSlug, string? appSlug)
    {
        var key = (Coalesce(clientSlug, _defaultClient), Coalesce(appSlug, _defaultApp));
        return _stores.GetOrAdd(key, k => new Lazy<RSCIReportStore>(() =>
        {
            // The app's own report tree + its own SQLite index, so the index acceleration + the
            // crash-stack/log-summary extraction (which the indexing decorator does on Save) work
            // per app — not just in the global SqliteIndex deployment.
            var appRoot = Path.Combine(_reportsRoot, "apps", k.Client, k.App);
            var fileStore = new RSCFileSystemReportStore(appRoot, _options, _loggerFactory.CreateLogger<RSCFileSystemReportStore>());
            return new RSCSqliteIndexingReportStore(fileStore, IndexFor(k), _loggerFactory.CreateLogger<RSCSqliteIndexingReportStore>());
        })).Value;
    }

    public RSCIReportIndexMaintenance GetMaintenance(string? clientSlug, string? appSlug) =>
        IndexFor((Coalesce(clientSlug, _defaultClient), Coalesce(appSlug, _defaultApp)));

    public void EvictClient(string clientSlug)
    {
        var client = Coalesce(clientSlug, _defaultClient);
        foreach (var key in _stores.Keys.Where(k => string.Equals(k.Client, client, StringComparison.Ordinal)).ToList())
            Remove(key);
    }

    public void EvictApp(string clientSlug, string appSlug)
        => Remove((Coalesce(clientSlug, _defaultClient), Coalesce(appSlug, _defaultApp)));

    /// <summary>Evict both the wrapping store handle and the shared SQLite index for one app, releasing
    /// the index's pooled connections (the filesystem store holds no connections).</summary>
    private void Remove((string Client, string App) key)
    {
        _stores.TryRemove(key, out _);
        if (_indexes.TryRemove(key, out var index) && index.IsValueCreated)
            index.Value.EvictPooledConnections();
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : RSCCatalogSlug.Normalize(value);
}
