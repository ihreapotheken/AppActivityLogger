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
    private const int TrendDays = 30;

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

        var activityTrend = await BuildActivityTrendAsync(platform, ct).ConfigureAwait(false);

        return new RSAAnalyticsDashboardVM(
            DailyActiveUsers: (int)Math.Min(int.MaxValue, totals.DailyActiveUsers),
            MonthlyActiveUsers: (int)Math.Min(int.MaxValue, totals.MonthlyActiveUsers),
            SessionsToday: (int)Math.Min(int.MaxValue, totals.SessionsToday),
            AverageSessionLength: totals.AverageSessionDuration,
            Platforms: perPlatform,
            TopScreens: topScreens,
            Retention: retention,
            ActivityTrend: activityTrend);
    }

    /// <summary>
    /// Builds the trailing-30-day daily activity series from the materialised daily rollups. The
    /// rollup table has one row per (day, platform); for the combined view we sum platforms into a
    /// single per-day point. Every day in the window is emitted (zero-filled) so the trend lines stay
    /// continuous even on days with no traffic.
    /// </summary>
    private async ValueTask<IReadOnlyList<RSAActivityPointVM>> BuildActivityTrendAsync(string? platform, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var firstDay = today.AddDays(-(TrendDays - 1));
        var from = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);

        var rollups = await _store.GetDailyRollupsAsync(from, until, platform, ct).ConfigureAwait(false);

        // Fold the per-platform rows into one bucket per calendar day.
        var byDay = new Dictionary<DateOnly, (long Users, long Sessions, long Events)>();
        foreach (var r in rollups)
        {
            byDay.TryGetValue(r.Day, out var acc);
            byDay[r.Day] = (acc.Users + r.DistinctUsers, acc.Sessions + r.Sessions, acc.Events + r.Events);
        }

        var points = new RSAActivityPointVM[TrendDays];
        for (var i = 0; i < TrendDays; i++)
        {
            var day = firstDay.AddDays(i);
            byDay.TryGetValue(day, out var v);
            points[i] = new RSAActivityPointVM(
                Label: day.ToString("MM-dd"),
                ActiveUsers: (int)Math.Min(int.MaxValue, v.Users),
                Sessions: (int)Math.Min(int.MaxValue, v.Sessions),
                Events: (int)Math.Min(int.MaxValue, v.Events));
        }
        return points;
    }
}
