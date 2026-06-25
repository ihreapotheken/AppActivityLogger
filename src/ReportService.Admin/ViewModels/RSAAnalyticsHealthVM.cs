using ReportService.Analytics;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Page model for /AnalyticsHealth — surfaces dead-letter counts, schema version, and a sample of
/// recent rejected events so SDK authors can spot drift without trawling logs.
/// </summary>
public sealed record RSAAnalyticsHealthVM(
    bool AnalyticsEnabled,
    int SchemaVersion,
    DateTimeOffset? LastAggregatedAt,
    RSCAnalyticsHealthSnapshot Snapshot,
    IReadOnlyList<RSCAnalyticsPlatformSummary> PlatformSummaries
);
