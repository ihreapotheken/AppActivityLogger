namespace ReportService.Analytics;

/// <summary>Per-batch outcome the ingestion endpoint hands back to the SDK: counts and the
/// canonical batch id (echoed so retries can be reconciled).</summary>
public sealed record RSCAnalyticsBatchReceipt(
    string BatchId,
    int AcceptedCount,
    int RejectedCount,
    int DuplicateCount,
    bool BatchRejected,
    string? BatchRejectReason
);

/// <summary>Per-platform summary used by the admin Status + Analytics pages.</summary>
public sealed record RSCAnalyticsPlatformSummary(
    string Platform,
    long AcceptedEvents,
    long RejectedEvents,
    long Batches,
    DateTimeOffset? LastReceivedAt
);

/// <summary>Tile-shape stats for the admin /Analytics root.</summary>
public sealed record RSCAnalyticsTotals(
    long DailyActiveUsers,
    long WeeklyActiveUsers,
    long MonthlyActiveUsers,
    long SessionsToday,
    long EventsToday,
    TimeSpan AverageSessionDuration,
    DateTimeOffset? LastAggregatedAt
);

/// <summary>One row of the daily rollup table.</summary>
public sealed record RSCAnalyticsDailyRollup(
    DateOnly Day,
    string Platform,
    long Events,
    long Sessions,
    long DistinctUsers
);

/// <summary>One row from the dead-letter table for the Health page.</summary>
public sealed record RSCAnalyticsDeadLetterRow(
    long Id,
    DateTimeOffset ReceivedAt,
    string Platform,
    string BatchId,
    string? EventId,
    string Reason,
    string? Detail,
    string RawJson
);

/// <summary>Top-screens slice rendered by the dashboard.</summary>
public sealed record RSCAnalyticsTopScreen(
    string Screen,
    long Views,
    TimeSpan AverageDuration
);

/// <summary>Health-page projection: counts grouped by reason, plus a few sample rows.</summary>
public sealed record RSCAnalyticsHealthSnapshot(
    long DeadLetterTotal,
    IReadOnlyDictionary<string, long> DeadLettersByReason,
    IReadOnlyList<RSCAnalyticsDeadLetterRow> RecentSamples,
    IReadOnlyDictionary<string, long> SdkVersionsSeen,
    DateTimeOffset? LastAggregatedAt
);

/// <summary>
/// Read-back projection from <c>analytics_events</c>. Carries the platform + identifier-hash that
/// the wire-shape <see cref="RSCAcceptedAnalyticsEvent"/> doesn't (those live on the batch
/// envelope at ingest time, but the storage row keeps them per-event for query convenience).
/// Used by the aggregation worker and the admin event-detail page.
/// </summary>
public sealed record RSCAnalyticsStoredEvent(
    string EventId,
    string Platform,
    string SessionId,
    string? AnonymousIdHash,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    string Name,
    string? Screen,
    string? Feature,
    long? DurationMs
);

/// <summary>Filter parameters for the admin event-search page.</summary>
public sealed record RSCAnalyticsEventFilter(
    string? Platform,
    string? Type,
    string? Name,
    string? Screen,
    string? SessionId,
    DateTimeOffset? From,
    DateTimeOffset? Until,
    int Limit,
    int Offset
);

/// <summary>One page of events for the admin /AnalyticsEvents page.</summary>
public sealed record RSCAnalyticsEventPage(
    IReadOnlyList<RSCAnalyticsStoredEvent> Rows,
    long Total,
    int Limit,
    int Offset
);

/// <summary>One row from analytics_sessions for the admin /AnalyticsSessions page.</summary>
public sealed record RSCAnalyticsSessionRow(
    string Platform,
    string SessionId,
    string? AnonymousIdHash,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    long EventCount,
    long ScreenCount
);
