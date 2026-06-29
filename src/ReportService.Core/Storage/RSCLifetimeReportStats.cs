namespace ReportService.Storage;

/// <summary>
/// Persistent, cumulative record of problem reports that have been deleted — folded in at deletion
/// time (operator delete, bulk delete, or a retention sweep) so the totals outlive the data they
/// describe. One row per platform; the admin dashboard sums them for a grand total and adds the
/// currently-retained counts to present a true "lifetime received" figure that never drops when
/// retention purges old reports.
/// </summary>
public sealed record RSCLifetimeReportStats(
    string Platform,
    long DeletedReports,
    long DeletedWithAttachment,
    long DeletedJsonBytes,
    long DeletedAttachmentBytes,
    DateTimeOffset? FirstDeletedAt,
    DateTimeOffset? LastDeletedAt)
{
    /// <summary>JSON + attachment bytes reclaimed across every deletion folded into this row.</summary>
    public long DeletedTotalBytes => DeletedJsonBytes + DeletedAttachmentBytes;
}
