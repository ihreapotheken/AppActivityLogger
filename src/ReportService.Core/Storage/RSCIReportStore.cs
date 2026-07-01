using ReportService.Models;

namespace ReportService.Storage;

/// <summary>
/// Persistence seam for Report-a-Problem submissions. Alternate back ends (in-memory for tests, the
/// SQLite-indexing decorator, a future cloud store) plug in here.
/// </summary>
public interface RSCIReportStore
{
    /// <summary>
    /// Persists the JSON document and optional gzip attachment under the canonical lowercase
    /// platform directory. JSON is written atomically from the in-memory bytes; the attachment is
    /// streamed. Identical (JSON, attachment) pairs are idempotent. <paramref name="ingestionChannel"/>
    /// tags the row so the admin can tell SDK multipart submissions apart from JSON-API submissions
    /// — must be one of the constants in <see cref="RSCIngestionChannels"/>.
    /// </summary>
    Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes, Stream? attachment, long? attachmentLength, string ingestionChannel, CancellationToken ct);

    /// <summary>Backward-compatible overload. Defaults <c>ingestionChannel</c> to <see cref="RSCIngestionChannels.Multipart"/>.</summary>
    Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes, Stream? attachment, long? attachmentLength, CancellationToken ct)
        => SaveAsync(report, jsonBytes, attachment, attachmentLength, RSCIngestionChannels.Multipart, ct);

    /// <summary>
    /// As the channel overload, but stamps the stored report — the filename timestamp <em>and</em> the
    /// indexed <c>SubmittedAt</c> — with an explicit <paramref name="submittedAt"/> instead of "now".
    /// The dev seeder uses this to backdate synthetic reports across a date window so the daily charts
    /// and "latest submissions" stay realistic; production ingestion uses the "now" overload. The
    /// default implementation ignores the override (alternate/test back ends need not implement it);
    /// the file / indexing / fan-out stores honour it.
    /// </summary>
    Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes, Stream? attachment, long? attachmentLength, string ingestionChannel, DateTimeOffset submittedAt, CancellationToken ct)
        => SaveAsync(report, jsonBytes, attachment, attachmentLength, ingestionChannel, ct);

    /// <summary>Lists stored reports for the given lowercase platform, newest first. Empty when the platform is unknown.</summary>
    IReadOnlyList<RSCStoredReport> List(string platform);

    /// <summary>Opens a stored JSON document or gzip attachment for reading, or null when unresolved.</summary>
    Stream? OpenRead(string platform, string fileName);

    /// <summary>Deletes the JSON document and its sibling attachment, if any. True when the JSON existed.</summary>
    bool Delete(string platform, string fileName);
}
