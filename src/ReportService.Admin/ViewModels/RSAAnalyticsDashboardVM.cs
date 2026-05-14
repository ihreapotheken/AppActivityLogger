namespace ReportService.Admin.ViewModels;

/// <summary>User-tracking analytics dashboard: engagement tiles, per-platform rows, top screens, retention.</summary>
public sealed record RSAAnalyticsDashboardVM(
    int DailyActiveUsers,
    int MonthlyActiveUsers,
    int SessionsToday,
    TimeSpan AverageSessionLength,
    IReadOnlyList<RSAAnalyticsPlatformRowVM> Platforms,
    IReadOnlyList<RSATopScreenVM> TopScreens,
    RSARetentionVM Retention);

public sealed record RSAAnalyticsPlatformRowVM(
    string Name,
    int DailyActiveUsers,
    int MonthlyActiveUsers,
    int SessionsToday,
    TimeSpan AverageSessionLength);

public sealed record RSATopScreenVM(
    string Screen,
    int Views,
    TimeSpan AverageTimeOnScreen);

public sealed record RSARetentionVM(
    double Day1,
    double Day7,
    double Day30);
