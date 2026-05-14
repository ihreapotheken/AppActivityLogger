namespace ReportService.Storage;

/// <summary>
/// Optional filter + pagination knobs for the admin reports list. Every field except
/// <see cref="Limit"/> / <see cref="Offset"/> is optional — null fields are ignored. Strings are
/// matched case-insensitively by the SQL backend.
/// </summary>
public sealed record RSCReportFilter(
    string? Platform = null,
    string? PharmacyId = null,
    string? UserId = null,
    string? Email = null,
    string? Phone = null,
    string? AppVersion = null,
    bool? HasAttachment = null,
    string? FileNameContains = null,
    DateTimeOffset? SubmittedFrom = null,
    DateTimeOffset? SubmittedUntil = null,
    string? IngestionChannel = null,
    // KindIn: if set, restrict to rows whose kind column matches one of the values.
    // KindNotIn: if set, exclude rows whose kind column matches any of the values — used by the
    // Problem-reports view to filter out crash + analytics submissions.
    IReadOnlyList<string>? KindIn = null,
    IReadOnlyList<string>? KindNotIn = null,
    // Exact match against the top_frame column. Set when an operator drills into a specific
    // signature from the Errors dashboard.
    string? TopFrame = null,
    int Limit = 50,
    int Offset = 0);
