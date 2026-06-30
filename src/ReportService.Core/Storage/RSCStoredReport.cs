namespace ReportService.Storage;

/// <summary>
/// Metadata describing a problem report persisted on disk. <c>AttachmentFileName</c> /
/// <c>AttachmentSizeBytes</c> are null when no gzip was uploaded. <c>IngestionChannel</c> is null
/// when the row is being surfaced from the file-system fallback (which can't recover the channel
/// without consulting the index).
/// </summary>
public sealed record RSCStoredReport(
    string Platform,
    string FileName,
    long SizeBytes,
    DateTimeOffset SubmittedAt,
    string? AttachmentFileName,
    long? AttachmentSizeBytes,
    string? IngestionChannel = null,
    string? TopFrame = null,
    string? LogSummaryJson = null,
    string? Kind = null,
    // Owning tenant (database-per-app). Stamped from the per-app store's own (client, app) identity
    // when a report is listed, so a fan-out view across apps can group/scope + route open/delete.
    string? ClientId = null,
    string? AppId = null
);
