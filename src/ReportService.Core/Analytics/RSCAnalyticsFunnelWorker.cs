using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Analytics;

/// <summary>
/// Background service that walks every enabled funnel definition on a fixed cadence and writes
/// the per-session step observations into <c>analytics_funnel_steps</c>. The matcher is
/// idempotent at the storage layer (INSERT OR IGNORE on the funnel-step primary key), so a
/// missed tick is recoverable and a re-run produces no extra rows.
/// </summary>
public sealed class RSCAnalyticsFunnelWorker : BackgroundService
{
    /// <summary>Look-back window for the funnel recompute. A session that crosses days still
    /// needs every step's event to land within the window for the matcher to see it; 14 days
    /// is comfortably longer than the typical "first session" funnel for OTC / cardlink flows
    /// without scanning the full <c>analytics_events</c> retention pool every tick.</summary>
    private const int FunnelWindowDays = 14;

    private readonly RSCIAnalyticsStoreFactory _factory;
    private readonly RSCICatalog _catalog;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsFunnelWorker> _logger;

    public RSCAnalyticsFunnelWorker(
        RSCIAnalyticsStoreFactory factory,
        RSCICatalog catalog,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsFunnelWorker> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Analytics disabled — funnel worker idle.");
            return;
        }

        // Funnel-definition seeding is per-app and folded into each app's tick below (idempotent,
        // INSERT-only), so a newly-registered app gets its definitions on the first tick that sees it.

        // Floor 5s (matches the aggregation worker) so the documented fast dev cadence
        // (FunnelIntervalSeconds=15 in appsettings.Development.json) is actually honoured rather
        // than silently clamped up to 60s. Production runs at 600s, well above the floor.
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.FunnelIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analytics funnel recompute failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Seeds the configured funnel definitions into one app's database (INSERT-only —
    /// never overwrites operator edits). Idempotent + safe to call every tick.</summary>
    internal static async Task SeedStoreAsync(
        RSCIAnalyticsStore store, RSCAnalyticsOptions options, ILogger logger, CancellationToken ct)
    {
        if (options.SeedFunnels.Length == 0) return;

        var existing = await store.ListFunnelDefinitionsAsync(onlyEnabled: false, ct).ConfigureAwait(false);
        var existingKeys = new HashSet<string>(existing.Select(d => d.FunnelKey), StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        foreach (var seed in options.SeedFunnels)
        {
            if (string.IsNullOrWhiteSpace(seed.FunnelKey)) continue;
            if (existingKeys.Contains(seed.FunnelKey)) continue;

            var steps = (seed.Steps ?? Array.Empty<RSCAnalyticsFunnelSeedStep>())
                .Where(s => !string.IsNullOrWhiteSpace(s.EventName))
                .Select(s => new RSCAnalyticsFunnelStep(
                    Name: string.IsNullOrWhiteSpace(s.Name) ? s.EventName : s.Name,
                    EventName: s.EventName,
                    EventType: string.IsNullOrWhiteSpace(s.EventType) ? null : s.EventType))
                .ToList();
            if (steps.Count == 0) continue;

            var def = new RSCAnalyticsFunnelDefinition(
                FunnelKey: seed.FunnelKey,
                DisplayName: string.IsNullOrWhiteSpace(seed.DisplayName) ? seed.FunnelKey : seed.DisplayName,
                Steps: steps,
                Enabled: true,
                CreatedAt: now,
                UpdatedAt: now);
            await store.UpsertFunnelDefinitionAsync(def, ct).ConfigureAwait(false);
            logger.LogInformation("Seeded funnel definition {Key} with {Steps} steps", def.FunnelKey, def.Steps.Count);
        }
    }

    /// <summary>Fan-out tick: seed (idempotent) + recompute funnels for every registered app's
    /// database. A per-app try/catch isolates one bad app DB. Returns total step observations.</summary>
    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var apps = await _catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false);
        var total = 0;
        foreach (var app in apps)
        {
            try
            {
                var store = _factory.Get(app.ClientSlug, app.Slug);
                await SeedStoreAsync(store, _options, _logger, ct).ConfigureAwait(false);
                total += await TickStoreAsync(store, _options, _logger, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Funnel recompute failed for app {Client}/{App}; skipping", app.ClientSlug, app.Slug);
            }
        }
        return total;
    }

    /// <summary>Recompute every enabled funnel for one app's database. Isolated for direct testing +
    /// fan-out reuse (the caller seeds definitions first).</summary>
    internal static async Task<int> TickStoreAsync(
        RSCIAnalyticsStore store, RSCAnalyticsOptions options, ILogger logger, CancellationToken ct)
    {
        var defs = await store.ListFunnelDefinitionsAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        if (defs.Count == 0)
        {
            logger.LogDebug("No enabled funnel definitions; nothing to recompute.");
            return 0;
        }

        var windowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-FunnelWindowDays);
        int total = 0;
        foreach (var def in defs)
        {
            try
            {
                var observed = await store.RecomputeFunnelStepsAsync(def, windowStart, ct).ConfigureAwait(false);
                total += observed;
                logger.LogInformation("Funnel {Key}: recorded {Count} step observations in {Window}-day window",
                    def.FunnelKey, observed, FunnelWindowDays);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Funnel {Key} recompute failed", def.FunnelKey);
            }
        }
        return total;
    }
}
