using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;

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

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsFunnelWorker> _logger;

    public RSCAnalyticsFunnelWorker(
        RSCIAnalyticsStore store,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsFunnelWorker> logger)
    {
        _store = store;
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

        try
        {
            await SeedDefinitionsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // Seeding failure should not stop the worker — operators can manage definitions
            // from the admin page once the page lands.
            _logger.LogError(ex, "Funnel seed failed; continuing without seeded definitions");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(60, _options.FunnelIntervalSeconds));

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

    private async Task SeedDefinitionsAsync(CancellationToken ct)
    {
        if (_options.SeedFunnels.Length == 0) return;

        var existing = await _store.ListFunnelDefinitionsAsync(onlyEnabled: false, ct).ConfigureAwait(false);
        var existingKeys = new HashSet<string>(existing.Select(d => d.FunnelKey), StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        foreach (var seed in _options.SeedFunnels)
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
            await _store.UpsertFunnelDefinitionAsync(def, ct).ConfigureAwait(false);
            _logger.LogInformation("Seeded funnel definition {Key} with {Steps} steps", def.FunnelKey, def.Steps.Count);
        }
    }

    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var defs = await _store.ListFunnelDefinitionsAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        if (defs.Count == 0)
        {
            _logger.LogDebug("No enabled funnel definitions; nothing to recompute.");
            return 0;
        }

        var windowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-FunnelWindowDays);
        int total = 0;
        foreach (var def in defs)
        {
            try
            {
                var observed = await _store.RecomputeFunnelStepsAsync(def, windowStart, ct).ConfigureAwait(false);
                total += observed;
                _logger.LogInformation("Funnel {Key}: recorded {Count} step observations in {Window}-day window",
                    def.FunnelKey, observed, FunnelWindowDays);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Funnel {Key} recompute failed", def.FunnelKey);
            }
        }
        return total;
    }
}
