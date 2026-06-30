namespace ReportService.Admin.ViewModels;

/// <summary>User-tracking analytics dashboard: engagement tiles, per-platform rows, top screens, retention.</summary>
public sealed record RSAAnalyticsDashboardVM(
    int DailyActiveUsers,
    int MonthlyActiveUsers,
    int SessionsToday,
    TimeSpan AverageSessionLength,
    IReadOnlyList<RSAAnalyticsPlatformRowVM> Platforms,
    IReadOnlyList<RSATopScreenVM> TopScreens,
    RSARetentionVM Retention,
    // Daily activity series for the "Activity over time" trend charts. Optional so the legacy
    // report-store implementation (which has no daily rollups) can omit it and render empty.
    IReadOnlyList<RSAActivityPointVM>? ActivityTrend = null);

/// <summary>One day of the engagement trend: distinct active users, sessions, and total events.
/// <see cref="Label"/> is the pre-formatted x-axis label ("MM-dd"), oldest → newest.</summary>
public sealed record RSAActivityPointVM(string Label, int ActiveUsers, int Sessions, int Events);

public sealed record RSAAnalyticsPlatformRowVM(
    string Name,
    int DailyActiveUsers,
    int MonthlyActiveUsers,
    int SessionsToday,
    TimeSpan AverageSessionLength,
    // Total analytics events recorded on this platform over the trailing 30 days, summed from the
    // daily rollups. Optional (defaults to 0) so the legacy report-store dashboard — which has no
    // rollup table — keeps compiling without supplying it.
    int EventsLast30Days = 0);

public sealed record RSATopScreenVM(
    string Screen,
    int Views,
    TimeSpan AverageTimeOnScreen);

public sealed record RSARetentionVM(
    double Day1,
    double Day7,
    double Day30);
