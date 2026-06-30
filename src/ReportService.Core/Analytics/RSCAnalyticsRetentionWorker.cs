using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Analytics;

/// <summary>
/// Background service that trims old <c>analytics_events</c> and <c>analytics_dead_letters</c>
/// rows on the schedule from <see cref="RSCAnalyticsOptions"/>, across every registered app's
/// database. Rollup tables are kept indefinitely — they're tiny and constitute the operator-facing
/// history.
/// </summary>
public sealed class RSCAnalyticsRetentionWorker : BackgroundService
{
    // Sweeps every hour; the per-row cutoff comes from the options. Matches the cadence of the
    // report-side retention sweep so operators have one rhythm to reason about.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly RSCIAnalyticsStoreFactory _factory;
    private readonly RSCICatalog _catalog;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsRetentionWorker> _logger;

    public RSCAnalyticsRetentionWorker(
        RSCIAnalyticsStoreFactory factory,
        RSCICatalog catalog,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsRetentionWorker> logger)
    {
        _factory = factory;
        _catalog = catalog;
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

                // Fan out over every app's DB; one app's failure is logged and skipped.
                var apps = await _catalog.ListAllAppsAsync(includeArchived: false, stoppingToken).ConfigureAwait(false);
                var purged = 0;
                foreach (var app in apps)
                {
                    try
                    {
                        purged += await _factory.Get(app.ClientSlug, app.Slug)
                            .PurgeOlderThanAsync(eventsCutoff, dlqCutoff, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Analytics retention sweep failed for app {Client}/{App}; skipping", app.ClientSlug, app.Slug);
                    }
                }
                if (purged > 0)
                {
                    _logger.LogInformation("Analytics retention purged {Count} rows across {Apps} apps", purged, apps.Count);
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
