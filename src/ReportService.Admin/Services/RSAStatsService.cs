using ReportService.Storage;

namespace ReportService.Admin.Services;

internal sealed class RSAStatsService : IRSAStatsService
{
    private readonly IRSAReportIndexAccessor _indexAccessor;

    public RSAStatsService(IRSAReportIndexAccessor indexAccessor)
    {
        _indexAccessor = indexAccessor;
    }

    public async Task<RSCStatsReport?> GetAsync(DateTimeOffset from, DateTimeOffset until, int topN, CancellationToken ct)
    {
        if (_indexAccessor.Maintenance is not { } maint) return null;
        try
        {
            return await maint.GetStatsAsync(from, until, topN, ct).ConfigureAwait(false);
        }
        catch
        {
            // Stats are non-critical; let the page render an "index degraded" notice instead of 5xx.
            return null;
        }
    }
}
