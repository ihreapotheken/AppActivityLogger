using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Read-side facade for the Stats page. In the database-per-app model it fans the window aggregates
/// out across the per-app SQLite indexes (scoped to the global <c>client</c>/<c>app</c> selection) and
/// merges them, so Stats agrees with the Dashboard and the per-app listing.
/// </summary>
public interface IRSAStatsService
{
    /// <summary>
    /// Aggregates for the supplied window, scoped to one tenant <paramref name="clientId"/> /
    /// <paramref name="appId"/> (null = all). Merged across every app in scope. Never null — an empty
    /// scope yields a zeroed report for the window.
    /// </summary>
    Task<RSCStatsReport> GetAsync(
        DateTimeOffset from, DateTimeOffset until, int topN,
        string? clientId, string? appId, CancellationToken ct);
}
