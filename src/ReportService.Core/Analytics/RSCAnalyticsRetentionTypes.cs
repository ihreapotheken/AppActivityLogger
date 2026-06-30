namespace ReportService.Analytics;

/// <summary>
/// One materialised retention cohort, produced by <see cref="RSCAnalyticsCohortWorker"/> and
/// rendered on <c>/AnalyticsRetention</c>. Counts are absolute (not percentages); the admin page
/// converts to percentages at render time.
/// </summary>
public sealed record RSCAnalyticsRetentionCohortRow(
    string AppId,
    string Environment,
    string ClientId,
    string Platform,
    DateOnly InstallDay,
    long CohortSize,
    long Day1Retained,
    long Day7Retained,
    long Day30Retained,
    int HashVersion,
    DateTimeOffset ComputedAt);

/// <summary>
/// Aggregated retention across a sliding window of cohorts. Daily counts sum across cohorts whose
/// install day is at least <c>offsetDays</c> in the past — D1 sums cohorts ≥ 1 day old, D7 sums
/// cohorts ≥ 7 days old, etc. Cohorts younger than the offset cannot have observed their full
/// retention window yet and are excluded.
/// </summary>
public sealed record RSCAnalyticsRetentionSummary(
    double Day1,
    double Day7,
    double Day30,
    long CohortsUsedDay1,
    long CohortsUsedDay7,
    long CohortsUsedDay30);
