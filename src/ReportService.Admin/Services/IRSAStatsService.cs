using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Read-side facade for the Stats page. Hides the SQLite-vs-disk fallback decision and the
/// optional <see cref="IRSAReportIndexAccessor"/> from the page model.
/// </summary>
public interface IRSAStatsService
{
    /// <summary>
    /// Returns aggregates for the supplied window, or <c>null</c> when no SQLite index is wired
    /// up (the disk store cannot answer aggregate queries efficiently).
    /// </summary>
    Task<RSCStatsReport?> GetAsync(DateTimeOffset from, DateTimeOffset until, int topN, CancellationToken ct);
}
