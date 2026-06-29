using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.Analytics;

/// <summary>
/// Background service that recomputes per-cohort D1/D7/D30 retention by walking
/// <c>analytics_user_days</c>. Writes <c>analytics_retention_cohorts</c> on each tick. The math
/// is idempotent (upserts) so a missed tick or a restart re-runs cleanly. Separate from the
/// row-purging <see cref="RSCAnalyticsRetentionWorker"/>, which trims raw events / dead-letters
/// by age — confusing name overlap, but two different jobs.
/// </summary>
public sealed class RSCAnalyticsCohortWorker : BackgroundService
{
    /// <summary>How far back the cohort recompute walks. 90 days lets a fresh D30 land for a
    /// cohort with up to 60 days of slack before its row stops getting updated.</summary>
    private const int CohortWindowDays = 90;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsCohortWorker> _logger;

    public RSCAnalyticsCohortWorker(
        RSCIAnalyticsStore store,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsCohortWorker> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Analytics disabled — cohort worker idle.");
            return;
        }

        // Floor 5s (matches the aggregation worker) so the documented fast dev cadence
        // (CohortIntervalSeconds=15 in appsettings.Development.json) is actually honoured rather
        // than silently clamped up to 60s. Production runs at 3600s, well above the floor.
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.CohortIntervalSeconds));

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
                _logger.LogError(ex, "Analytics cohort recompute failed");
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

    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var windowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-CohortWindowDays);
        var count = await _store.RecomputeRetentionCohortsAsync(
            windowStart, _options.IdentifierHashVersion, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Retention cohorts recomputed: {Count} (platform, install_day) rows updated, window from {From}",
            count, windowStart);
        return count;
    }
}
