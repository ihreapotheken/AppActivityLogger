using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Catalog;

namespace ReportService.Analytics;

/// <summary>
/// Resolves the analytics store for a given <c>(client, app)</c> in the database-per-app model — each
/// app owns its own <c>analytics.db</c>. Resolution is the hot path on ingestion + every dashboard
/// read, so it's a cached lookup; the per-app DB is created and migrated once, on first use.
/// </summary>
public interface RSCIAnalyticsStoreFactory
{
    /// <summary>The store for one app's database, provisioning + migrating it on first use. Null/blank
    /// slugs coalesce to the configured default client/app so attribution-omitting traffic resolves.</summary>
    RSCSqliteAnalyticsStore Get(string? clientSlug, string? appSlug);

    /// <summary>The default app's store (the configured default client/app) — the landing bucket for
    /// traffic with no explicit attribution and the back-compat target for legacy single-DB callers.</summary>
    RSCSqliteAnalyticsStore GetDefault();
}

/// <summary>
/// SQLite implementation. Caches one <see cref="RSCSqliteAnalyticsStore"/> per <c>(client, app)</c>
/// behind a <see cref="Lazy{T}"/> in a <see cref="ConcurrentDictionary{TKey,TValue}"/>, so the
/// migration ladder (<c>Bootstrap()</c>) runs <b>exactly once per app</b> even under concurrent
/// first-touch ingestion — never per batch, no double-bootstrap race. Each store opens/closes its own
/// connections per operation (WAL + busy-retry), so the cached set is a bounded handle footprint, not
/// one-open-connection-per-app.
/// </summary>
public sealed class RSCSqliteAnalyticsStoreFactory : RSCIAnalyticsStoreFactory
{
    private readonly string _reportsRoot;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly string _defaultClient;
    private readonly string _defaultApp;
    private readonly ILogger<RSCSqliteAnalyticsStore> _logger;

    private readonly ConcurrentDictionary<(string Client, string App), Lazy<RSCSqliteAnalyticsStore>> _stores = new();

    public RSCSqliteAnalyticsStoreFactory(
        RSCReportServiceOptions reportOptions,
        RSCAnalyticsOptions analyticsOptions,
        RSCCatalogOptions catalogOptions,
        ILogger<RSCSqliteAnalyticsStore> logger)
    {
        _reportsRoot = reportOptions.ReportsRoot;
        _analyticsOptions = analyticsOptions;
        _logger = logger;
        _defaultClient = Coalesce(catalogOptions.DefaultClientSlug, "default");
        _defaultApp = Coalesce(catalogOptions.DefaultAppSlug, "default");
    }

    public RSCSqliteAnalyticsStore Get(string? clientSlug, string? appSlug)
    {
        var client = Coalesce(clientSlug, _defaultClient);
        var app = Coalesce(appSlug, _defaultApp);
        return _stores.GetOrAdd((client, app), key => new Lazy<RSCSqliteAnalyticsStore>(() =>
        {
            var dbPath = RSCStatePaths.ResolveAppDb(_reportsRoot, key.Client, key.App, "analytics.db");
            _logger.LogInformation("Provisioning per-app analytics store for {Client}/{App} at {Path}", key.Client, key.App, dbPath);
            return new RSCSqliteAnalyticsStore(dbPath, _analyticsOptions, _logger);
        })).Value;
    }

    public RSCSqliteAnalyticsStore GetDefault() => Get(_defaultClient, _defaultApp);

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? RSCCatalogSlug.Normalize(fallback) : RSCCatalogSlug.Normalize(value);
}
