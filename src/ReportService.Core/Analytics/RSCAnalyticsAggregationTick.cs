namespace ReportService.Analytics;

/// <summary>
/// Precomputed input for one atomic aggregation tick. The worker groups the unaggregated event
/// pool by (platform, day) and per session, derives deltas, then hands the whole bundle to
/// <see cref="RSCIAnalyticsStore.WriteAggregationTickAsync"/>. The store applies every delta and
/// marks the source events aggregated in one SQLite transaction — a crash before commit leaves
/// the pool unchanged, so replay never doubles counters.
/// </summary>
public sealed record RSCAnalyticsAggregationTick(
    IReadOnlyList<RSCAggregationSessionDelta> Sessions,
    IReadOnlyList<RSCAggregationUserDayDelta> UserDays,
    IReadOnlyList<RSCAggregationDailyRollupDelta> DailyRollups,
    IReadOnlyList<string> EventIds);

public sealed record RSCAggregationSessionDelta(
    string Platform,
    string SessionId,
    string? AnonymousIdHash,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    long EventCount,
    long ScreenCount);

public sealed record RSCAggregationUserDayDelta(
    string Platform,
    DateOnly Day,
    string AnonymousIdHash,
    int HashVersion,
    long Events);

public sealed record RSCAggregationDailyRollupDelta(
    DateOnly Day,
    string Platform,
    long Events,
    long Sessions,
    long DistinctUsers);
