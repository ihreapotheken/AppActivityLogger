namespace ReportService.Storage;

/// <summary>Per-platform aggregate row for the admin dashboard.</summary>
public sealed record RSCPlatformSummary(string Platform, int ReportCount, long TotalSizeBytes, long TotalAttachmentBytes, DateTimeOffset? NewestSubmittedAt);
