namespace ReportService.Audit;

/// <summary>
/// A single audited operator action. <c>Details</c> is a short human-readable payload — never the
/// secret itself. <c>Actor</c> is the authenticated operator (currently always <c>"operator"</c>)
/// or <c>"anonymous"</c> for pre-login events.
/// </summary>
public sealed record RSCAuditEntry(
    DateTimeOffset At,
    string Actor,
    string RemoteAddress,
    string Action,
    string? Target,
    string? Details,
    bool Success);
