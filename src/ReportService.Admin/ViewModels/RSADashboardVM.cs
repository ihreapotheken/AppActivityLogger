namespace ReportService.Admin.ViewModels;

/// <summary>Dashboard summary: tiles, per-platform×channel counts, latest activity.</summary>
public sealed record RSADashboardVM(
    int TotalCount,
    int MultipartCount,
    int JsonCount,
    long TotalJsonBytes,
    long TotalAttachmentBytes,
    IReadOnlyList<RSAPlatformRowVM> Platforms,
    IReadOnlyList<RSAReportRowVM> Recent);

/// <summary>Per-platform row on the dashboard with the multipart vs JSON channel split.</summary>
public sealed record RSAPlatformRowVM(
    string Name,
    int Count,
    int MultipartCount,
    int JsonCount,
    DateTimeOffset? LatestSubmittedAt);
