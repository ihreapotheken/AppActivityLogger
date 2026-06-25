using ReportService.Analytics;

namespace ReportService.Ingestion;

/// <summary>
/// Outcome of an analytics ingestion attempt. The endpoint maps <see cref="HttpStatus"/> to the
/// outgoing response; <see cref="Receipt"/> is non-null on accept (and may report internal
/// validator rejections — see <see cref="RSCAnalyticsBatchReceipt.BatchRejected"/>).
/// </summary>
public sealed record RSAnalyticsIngestionResult(
    bool Success,
    int HttpStatus,
    string? Error,
    RSCAnalyticsBatchReceipt? Receipt)
{
    public static RSAnalyticsIngestionResult Accepted(RSCAnalyticsBatchReceipt receipt) =>
        new(true, 202, null, receipt);

    public static RSAnalyticsIngestionResult BadRequest(string reason) =>
        new(false, 400, reason, null);

    public static RSAnalyticsIngestionResult PayloadTooLarge(string reason) =>
        new(false, 413, reason, null);

    public static RSAnalyticsIngestionResult UnsupportedMediaType(string reason) =>
        new(false, 415, reason, null);

    public static RSAnalyticsIngestionResult ServiceUnavailable(string reason) =>
        new(false, 503, reason, null);
}
