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
    string? Kind = null
);
