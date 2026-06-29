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
    IReadOnlyList<RSCAggregationEventRef> Events);

/// <summary>
/// Identity of one source event the tick is marking aggregated. The table is keyed by
/// <c>UNIQUE(platform, event_id)</c>, so the same <c>event_id</c> can legitimately exist on two
/// platforms (mobile collisions, and the backend server-events path where callers control ids).
/// Carrying <see cref="Platform"/> alongside <see cref="EventId"/> lets the mark target exactly
/// the row that was aggregated instead of every row sharing the id.
/// </summary>
public sealed record RSCAggregationEventRef(
    string Platform,
    string EventId);

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
