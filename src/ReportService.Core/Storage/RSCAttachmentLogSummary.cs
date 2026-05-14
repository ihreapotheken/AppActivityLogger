namespace ReportService.Storage;

/// <summary>
/// Structured rollup the indexing store extracts from a gzip log attachment when the payload
/// looks like a plaintext JSON array (iOS SDK shape). Persisted as JSON in
/// <c>problem_reports.log_summary_json</c> so the admin's report-detail page can render counts
/// per level + http-event total without redoing the gzip decode on every render. Android
/// attachments are AES-encrypted client-side; for those rows the summary stays null and the
/// detail view falls back to "encrypted logcat — N bytes" instead.
/// </summary>
public sealed record RSCAttachmentLogSummary(
    int TotalEntries,
    IReadOnlyDictionary<string, int> ByLevel,
    int HttpEventCount,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest);
