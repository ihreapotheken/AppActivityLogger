using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Options;

namespace ReportService.Admin.Services;

/// <summary>
/// Backs the <c>/Analytics</c> page from <see cref="RSCIAnalyticsStore"/> rollup tables instead of
/// scanning every persisted problem-report JSON. Numbers come from the aggregation worker's
/// materialized state, so a page render is O(rollup rows) rather than O(stored reports × bytes).
/// </summary>
/// <remarks>
/// Fallback: when the analytics tables are empty (fresh install, or analytics disabled mid-rollout),
/// the page would render zeros across the board. We could chain through to the legacy
/// report-scanning implementation as a fallback, but doing so silently mixes two data sources in
/// one tile — operators couldn't tell which numbers came from where. Better to show "0" and let
/// the Health page explain that no batches have been received yet.
/// </remarks>
public sealed class RSAAnalyticsStoreDashboardService : IRSAAnalyticsDashboardService
{
    private const int TopScreens = 5;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCReportServiceOptions _reportOptions;

    public RSAAnalyticsStoreDashboardService(
        RSCIAnalyticsStore store,
        RSCReportServiceOptions reportOptions)
    {
        _store = store;
        _reportOptions = reportOptions;
    }

    public RSAAnalyticsDashboardVM Build(string? platform = null)
    {
        // Synchronous shim for callers that don't await. SQLite reads are fast enough that
        // briefly blocking is fine for a low-frequency admin page render.
        return BuildAsync(platform, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask<RSAAnalyticsDashboardVM> BuildAsync(string? platform, CancellationToken ct)
    {
        var totals = await _store.GetTotalsAsync(platform, ct).ConfigureAwait(false);

        // Per-platform tiles: GetTotalsAsync gives the global totals; we need per-platform numbers
        // so the table beneath the tiles shows where activity is coming from.
        var perPlatform = new List<RSAAnalyticsPlatformRowVM>();
        foreach (var p in _reportOptions.AllowedPlatforms)
        {
            if (platform is { Length: > 0 } scope && !string.Equals(p, scope, StringComparison.OrdinalIgnoreCase))
                continue;

            var t = await _store.GetTotalsAsync(p, ct).ConfigureAwait(false);
            perPlatform.Add(new RSAAnalyticsPlatformRowVM(
                Name: p,
                DailyActiveUsers: (int)Math.Min(int.MaxValue, t.DailyActiveUsers),
                MonthlyActiveUsers: (int)Math.Min(int.MaxValue, t.MonthlyActiveUsers),
                SessionsToday: (int)Math.Min(int.MaxValue, t.SessionsToday),
                AverageSessionLength: t.AverageSessionDuration));
        }

        var screens = await _store.GetTopScreensAsync(platform, TopScreens, ct).ConfigureAwait(false);
        var topScreens = screens
            .Select(s => new RSATopScreenVM(s.Screen, (int)Math.Min(int.MaxValue, s.Views), s.AverageDuration))
            .ToArray();

        // Retention comes from the cohort worker's materialised analytics_retention_cohorts.
        // The summary is cohort-weighted across the last 60 days and skips cohorts too young to
        // have observed each window (D1 needs 1+ day, D7 needs 7+, D30 needs 30+). Until the
        // worker has run at least once the summary is honest-zero — no cohorts to weigh.
        var summary = await _store.GetRetentionSummaryAsync(platform, windowDays: 60, ct).ConfigureAwait(false);
        var retention = new RSARetentionVM(
            Day1:  summary.Day1,
            Day7:  summary.Day7,
            Day30: summary.Day30);

        return new RSAAnalyticsDashboardVM(
            DailyActiveUsers: (int)Math.Min(int.MaxValue, totals.DailyActiveUsers),
            MonthlyActiveUsers: (int)Math.Min(int.MaxValue, totals.MonthlyActiveUsers),
            SessionsToday: (int)Math.Min(int.MaxValue, totals.SessionsToday),
            AverageSessionLength: totals.AverageSessionDuration,
            Platforms: perPlatform,
            TopScreens: topScreens,
            Retention: retention);
    }
}
