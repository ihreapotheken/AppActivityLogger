using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.Storage.Retention;

/// <summary>
/// Periodic timer that drives <see cref="RSCRetentionService.SweepAsync"/>. Lives in the ingestion
/// process only — the admin process can call <see cref="RSCRetentionService"/> directly when an
/// operator clicks "Purge now", but it must not double-sweep concurrently with the worker (the
/// service's internal semaphore makes both paths safe regardless).
/// </summary>
public sealed class RSCRetentionBackgroundService : BackgroundService
{
    private readonly RSCRetentionService _retention;
    private readonly RSCReportServiceOptions _options;
    private readonly ILogger<RSCRetentionBackgroundService> _logger;

    public RSCRetentionBackgroundService(
        RSCRetentionService retention,
        RSCReportServiceOptions options,
        ILogger<RSCRetentionBackgroundService> logger)
    {
        _retention = retention;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RetentionEnabled)
        {
            _logger.LogInformation("Retention background sweep disabled (ReportService:RetentionEnabled=false)");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(60, _options.RetentionScanIntervalSeconds));
        _logger.LogInformation(
            "Retention background sweep enabled: cap={Cap} bytes, max_age={Days} days, interval={Interval}",
            _options.RetentionMaxBytes, _options.RetentionMaxAgeDays, interval);

        // Stagger the first sweep slightly so startup isn't competing for IO with the first ingests.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await _retention.SweepAsync("retention.background", stoppingToken).ConfigureAwait(false);
                if (report.DidWork)
                {
                    _logger.LogInformation(
                        "Retention sweep complete: deleted={Total} bytes={Bytes} after={After}/{Cap}",
                        report.DeletedTotal, report.DeletedBytes, report.BytesAfter, report.LimitBytes);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never bring the host down. Log and try again next interval.
                _logger.LogError(ex, "Retention sweep failed; will retry after {Interval}", interval);
            }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
