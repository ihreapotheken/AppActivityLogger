using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.Analytics;

/// <summary>
/// Background service that trims old <c>analytics_events</c> and <c>analytics_dead_letters</c>
/// rows on the schedule from <see cref="RSCAnalyticsOptions"/>. Rollup tables are kept indefinitely
/// — they're tiny and constitute the operator-facing history.
/// </summary>
public sealed class RSCAnalyticsRetentionWorker : BackgroundService
{
    // Sweeps every hour; the per-row cutoff comes from the options. Matches the cadence of the
    // report-side retention sweep so operators have one rhythm to reason about.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsRetentionWorker> _logger;

    public RSCAnalyticsRetentionWorker(
        RSCIAnalyticsStore store,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsRetentionWorker> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var eventsCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.RawEventRetentionDays));
                var dlqCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.DeadLetterRetentionDays));
                var purged = await _store.PurgeOlderThanAsync(eventsCutoff, dlqCutoff, stoppingToken)
                    .ConfigureAwait(false);
                if (purged > 0)
                {
                    _logger.LogInformation("Analytics retention purged {Count} rows", purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analytics retention sweep failed");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
