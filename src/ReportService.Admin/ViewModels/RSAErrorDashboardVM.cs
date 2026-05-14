namespace ReportService.Admin.ViewModels;

/// <summary>Error-reporting dashboard: crash + error tiles, per-platform rows, top error types, recent reports.</summary>
public sealed record RSAErrorDashboardVM(
    int CrashesLast24h,
    int ErrorsLast24h,
    int AffectedUsers,
    IReadOnlyList<RSAErrorPlatformRowVM> Platforms,
    IReadOnlyList<RSATopErrorVM> TopErrors,
    IReadOnlyList<int> ErrorRateLast7Days,
    IReadOnlyList<RSARecentErrorVM> RecentErrors);

public sealed record RSAErrorPlatformRowVM(
    string Name,
    int CrashesLast24h,
    int ErrorsLast24h,
    int AffectedUsers);

/// <summary>
/// One row of the "top errors" table. <see cref="Signature"/> is the dedup key — either the
/// top stack frame (preferred, set at ingest from the gzip attachment) or the truncated message
/// when no stack trace is available. <see cref="MultipartCount"/> + <see cref="JsonCount"/> sum
/// to <see cref="Occurrences"/> and let the operator see at a glance which ingestion paths are
/// hitting this fault site.
/// </summary>
public sealed record RSATopErrorVM(
    string Signature,
    int Occurrences,
    int AffectedUsers,
    int MultipartCount,
    int JsonCount);

/// <summary>
/// One row of the "recent errors" feed. <see cref="Signature"/> is the same per-row identifier
/// used in the top-errors table, so an operator can correlate a recent occurrence with its
/// rolled-up bucket without a second lookup. <see cref="Channel"/> tags the ingestion path
/// (multipart / json) — surfaced as a small badge in the row.
/// </summary>
public sealed record RSARecentErrorVM(
    DateTimeOffset OccurredAt,
    string Platform,
    string Signature,
    string Channel);
