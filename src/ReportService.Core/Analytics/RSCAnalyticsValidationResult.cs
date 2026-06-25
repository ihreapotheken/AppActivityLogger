using ReportService.Models;

namespace ReportService.Analytics;

/// <summary>
/// Outcome of <see cref="RSCAnalyticsValidator"/> running over one batch. <see cref="Accepted"/>
/// rows are ready to be inserted into <c>analytics_events</c>; <see cref="Rejected"/> rows are
/// ready to be inserted into <c>analytics_dead_letters</c>. A batch can be wholly accepted, wholly
/// rejected, or partially split — the ingestion service decides what to do with each list.
/// </summary>
public sealed record RSCAnalyticsValidationResult(
    bool BatchRejected,
    string? BatchRejectReason,
    IReadOnlyList<RSCAcceptedAnalyticsEvent> Accepted,
    IReadOnlyList<RSCRejectedAnalyticsEvent> Rejected
);

/// <summary>
/// A validated event ready to land in <c>analytics_events</c>. Carries the normalized timestamp,
/// hashed identifiers, and the original property bag with PII keys already stripped.
/// </summary>
public sealed record RSCAcceptedAnalyticsEvent(
    string EventId,
    string SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    string Name,
    string? Screen,
    string? Feature,
    long? DurationMs,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<RSCAnalyticsItem> Items
);

/// <summary>
/// An event the validator wouldn't admit, paired with the reason from
/// <see cref="RSCAnalyticsDeadLetterReasons"/>. The original raw event JSON is preserved so the
/// admin Health page can show a sample.
/// </summary>
public sealed record RSCRejectedAnalyticsEvent(
    string? EventId,
    string Reason,
    string? Detail,
    string RawJson
);
