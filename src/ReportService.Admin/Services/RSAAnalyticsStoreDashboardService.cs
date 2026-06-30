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

    public RSAAnalyticsDashboardVM Build(RSCAnalyticsScope scope = default)
    {
        // Synchronous shim for callers that don't await. SQLite reads are fast enough that
        // briefly blocking is fine for a low-frequency admin page render.
        return BuildAsync(scope, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask<RSAAnalyticsDashboardVM> BuildAsync(RSCAnalyticsScope scope, CancellationToken ct)
    {
        var totals = await _store.GetTotalsAsync(scope, ct).ConfigureAwait(false);

        // Trailing-30-day daily rollups, fetched once and used twice: the activity trend lines and
        // the per-platform 30-day event totals (the "events by platform" composition donut).
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var firstDay = today.AddDays(-(TrendDays - 1));
        var from = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
        var rollups = await _store.GetDailyRollupsAsync(from, until, scope, ct).ConfigureAwait(false);

        // 30-day event count per platform, summed across the window's daily rollup rows.
        var eventsByPlatform = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rollups)
        {
            eventsByPlatform.TryGetValue(r.Platform, out var acc);
            eventsByPlatform[r.Platform] = acc + r.Events;
        }

        // Per-platform tiles: GetTotalsAsync gives the global totals; we need per-platform numbers
        // so the table beneath the tiles shows where activity is coming from.
        var perPlatform = new List<RSAAnalyticsPlatformRowVM>();
        foreach (var p in _reportOptions.AllowedPlatforms)
        {
            if (scope.Platform is { Length: > 0 } sel && !string.Equals(p, sel, StringComparison.OrdinalIgnoreCase))
                continue;

            // Same app/env/client scope, narrowed to this one platform.
            var t = await _store.GetTotalsAsync(scope with { Platform = p }, ct).ConfigureAwait(false);
            eventsByPlatform.TryGetValue(p, out var events30);
            perPlatform.Add(new RSAAnalyticsPlatformRowVM(
                Name: p,
                DailyActiveUsers: (int)Math.Min(int.MaxValue, t.DailyActiveUsers),
                MonthlyActiveUsers: (int)Math.Min(int.MaxValue, t.MonthlyActiveUsers),
                SessionsToday: (int)Math.Min(int.MaxValue, t.SessionsToday),
                AverageSessionLength: t.AverageSessionDuration,
                EventsLast30Days: (int)Math.Min(int.MaxValue, events30)));
        }

        var screens = await _store.GetTopScreensAsync(scope, TopScreens, ct).ConfigureAwait(false);
        var topScreens = screens
            .Select(s => new RSATopScreenVM(s.Screen, (int)Math.Min(int.MaxValue, s.Views), s.AverageDuration))
            .ToArray();

        // Retention comes from the cohort worker's materialised analytics_retention_cohorts.
        // The summary is cohort-weighted across the last 60 days and skips cohorts too young to
        // have observed each window (D1 needs 1+ day, D7 needs 7+, D30 needs 30+). Until the
        // worker has run at least once the summary is honest-zero — no cohorts to weigh.
        var summary = await _store.GetRetentionSummaryAsync(scope, windowDays: 60, ct).ConfigureAwait(false);
        var retention = new RSARetentionVM(
            Day1:  summary.Day1,
            Day7:  summary.Day7,
            Day30: summary.Day30);

        var activityTrend = BuildActivityTrend(rollups, firstDay);

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
    /// continuous even on days with no traffic. Pure: the caller fetches the rollups once and shares
    /// them with the per-platform event totals.
    /// </summary>
    private static IReadOnlyList<RSAActivityPointVM> BuildActivityTrend(
        IReadOnlyList<RSCAnalyticsDailyRollup> rollups, DateOnly firstDay)
    {
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
