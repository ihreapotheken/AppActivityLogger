using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.DeepLinks;

/// <summary>
/// Background service that purges recorded deep-link clicks older than the configured retention.
/// The retention period is resolved per sweep as the operator-set override (persisted in the
/// deep-link DB via the admin endpoint/page) falling back to
/// <see cref="RSCDeepLinkOptions.ClickRetentionDays"/>. Link definitions are never touched — only
/// the click stream, which is the part that grows unbounded under a public smart link.
/// </summary>
public sealed class RSCDeepLinkClickRetentionWorker : BackgroundService
{
    private readonly RSCIDeferredDeepLinkStore _store;
    private readonly RSCDeepLinkOptions _options;
    private readonly ILogger<RSCDeepLinkClickRetentionWorker> _logger;

    public RSCDeepLinkClickRetentionWorker(
        RSCIDeferredDeepLinkStore store,
        RSCDeepLinkOptions options,
        ILogger<RSCDeepLinkClickRetentionWorker> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Floor at 60s so a mis-set interval can't spin the sweep.
        var interval = TimeSpan.FromSeconds(Math.Max(60, _options.RetentionScanIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deep-link click retention sweep failed");
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

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var persisted = await _store.GetClickRetentionDaysAsync(ct).ConfigureAwait(false);
        var days = Math.Max(1, persisted ?? _options.ClickRetentionDays);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var purged = await _store.PurgeClicksOlderThanAsync(cutoff, ct).ConfigureAwait(false);
        if (purged > 0)
            _logger.LogInformation("Deep-link retention purged {Count} clicks older than {Days}d", purged, days);
        return purged;
    }
}
