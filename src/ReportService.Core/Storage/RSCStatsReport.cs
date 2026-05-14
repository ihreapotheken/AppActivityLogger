namespace ReportService.Storage;

/// <summary>
/// Aggregates produced by <see cref="RSCIReportIndexMaintenance.GetStatsAsync"/> for the admin
/// Stats overview. All counts are scoped to the supplied <c>From</c>/<c>Until</c> window
/// (inclusive of <c>From</c>, exclusive of <c>Until</c>) so the same query powers both
/// "last 30 days" and arbitrary operator-specified windows.
/// </summary>
public sealed record RSCStatsReport(
    DateTimeOffset From,
    DateTimeOffset Until,
    int TotalReports,
    int MultipartCount,
    int JsonCount,
    long TotalJsonBytes,
    long TotalAttachmentBytes,
    IReadOnlyList<RSCDailyVolume> Daily,
    IReadOnlyList<RSCStatsBucket> ByDeviceModel,
    IReadOnlyList<RSCStatsBucket> ByPharmacy,
    IReadOnlyList<RSCStatsBucket> ByAppVersion,
    IReadOnlyList<RSCStatsBucket> ByPlatform,
    IReadOnlyList<RSCStatsBucket> ByChannel);

/// <summary>One day's submission volume, split by ingestion channel.</summary>
public sealed record RSCDailyVolume(DateOnly Date, int Multipart, int Json)
{
    public int Total => Multipart + Json;
}

/// <summary>Generic key + count row used for top-N breakdowns (top types, top devices, …).</summary>
public sealed record RSCStatsBucket(string Key, int Count);
