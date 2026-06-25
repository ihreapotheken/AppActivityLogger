namespace ReportService.Analytics;

/// <summary>
/// One operator-defined funnel. The <see cref="Steps"/> are evaluated in order — a session
/// "reaches" step <c>k</c> if it has events matching steps <c>0..k</c> in the right temporal
/// order (each step's earliest match must be at or after the previous step's match).
/// </summary>
public sealed record RSCAnalyticsFunnelDefinition(
    string FunnelKey,
    string DisplayName,
    IReadOnlyList<RSCAnalyticsFunnelStep> Steps,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One funnel step matcher. <see cref="EventName"/> is exact-match on the event's <c>name</c>;
/// <see cref="EventType"/> when set further filters by event kind (e.g. <c>"screen"</c>).
/// Keeping the matcher this thin matches the validator's strict-on-shape philosophy — operator
/// intent is unambiguous on the page.
/// </summary>
public sealed record RSCAnalyticsFunnelStep(
    string Name,
    string EventName,
    string? EventType);

/// <summary>One row of the funnel admin page — step-by-step reached count per funnel.</summary>
public sealed record RSCAnalyticsFunnelStepStat(
    int StepIndex,
    string StepName,
    long SessionsReached);
