namespace ReportService.Storage;

/// <summary>
/// Index-friendly projection of a persisted <see cref="Models.RSCProblemReport"/>. The raw contact
/// email is deliberately excluded — only its SHA-256 hex digest is stored in <c>EmailHash</c>.
/// <c>IngestionChannel</c> distinguishes the multipart SDK path from the JSON API path.
/// </summary>
public sealed record RSCReportMetadata(
    string Platform,
    string FileName,
    DateTimeOffset SubmittedAt,
    string? DeviceModel,
    string? Title,
    string? EmailHash,
    string? PharmacyId,
    string? AppVersion,
    bool HasAttachment,
    long SizeBytes,
    long? AttachmentSizeBytes,
    string? LabelsJson,
    string IngestionChannel = RSCIngestionChannels.Multipart,
    string? TopFrame = null,
    string? UserId = null,
    string? Phone = null,
    string? LogSummaryJson = null,
    string? Kind = null
);
