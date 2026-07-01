using ReportService.Analytics;

namespace ReportService.Ingestion;

/// <summary>
/// Outcome of an analytics ingestion attempt. The endpoint maps <see cref="HttpStatus"/> to the
/// outgoing response; <see cref="Receipt"/> is non-null once a batch has been through the store
/// (whether accepted, partially rejected, or fully rejected) so the caller always gets per-event
/// detail — even on the 400 emitted for a fully-rejected batch.
/// </summary>
public sealed record RSAnalyticsIngestionResult(
    bool Success,
    int HttpStatus,
    string? Error,
    RSCAnalyticsBatchReceipt? Receipt)
{
    /// <summary>
    /// The single, project-wide rule for turning a stored batch receipt into an HTTP outcome, shared
    /// by BOTH the SDK and server ingestion paths so the 2xx-vs-4xx decision can never drift between
    /// them. A <b>fully-rejected</b> batch — rejected at the batch level (unknown client/app/platform,
    /// bad schema, oversize, empty), or with every event dead-lettered and nothing accepted — is a
    /// <b>400</b>, never a 202 that would mask a total failure. It still carries the receipt so the
    /// caller can see exactly what bounced. A partial accept, or an idempotent all-duplicates replay
    /// (nothing accepted but nothing rejected either), stays 202 with the receipt.
    /// </summary>
    public static RSAnalyticsIngestionResult FromReceipt(RSCAnalyticsBatchReceipt receipt) =>
        IsFullyRejected(receipt)
            ? new(false, 400, receipt.BatchRejectReason ?? "all events rejected", receipt)
            : new(true, 202, null, receipt);

    /// <summary>True when nothing usable landed: a batch-level rejection, or every event rejected
    /// with zero accepted. All-duplicate replays (accepted 0, rejected 0) are NOT fully rejected.</summary>
    public static bool IsFullyRejected(RSCAnalyticsBatchReceipt receipt) =>
        receipt.BatchRejected || (receipt.AcceptedCount == 0 && receipt.RejectedCount > 0);

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
