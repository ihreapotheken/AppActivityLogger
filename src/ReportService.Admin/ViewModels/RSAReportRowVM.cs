using ReportService.Storage;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// View-side projection of a stored report. The page templates bind to this DTO instead of the
/// storage-layer <c>RSCStoredReport</c>, so a column rename in the persistence layer does not
/// silently break Razor binding.
/// </summary>
public sealed record RSAReportRowVM(
    string Platform,
    string FileName,
    DateTimeOffset SubmittedAt,
    long SizeBytes,
    string? AttachmentFileName,
    long? AttachmentSizeBytes,
    string Channel,
    string ChannelLabel,
    string? Kind = null,
    string? TopFrame = null,
    RSCAttachmentLogSummary? LogSummary = null);
