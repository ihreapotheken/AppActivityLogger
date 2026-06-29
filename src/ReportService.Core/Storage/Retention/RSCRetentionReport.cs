namespace ReportService.Storage.Retention;

/// <summary>
/// Outcome of one retention sweep. Counts are split by reason so the audit log + dashboard can
/// show "12 reports purged: 4 over the 30-day cutoff, 8 because the store was at 102% of cap".
/// </summary>
public sealed record RSCRetentionReport(
    int DeletedByAge,
    int DeletedBySize,
    long DeletedBytes,
    long BytesBefore,
    long BytesAfter,
    long LimitBytes,
    int? MaxAgeDays,
    DateTimeOffset At,
    TimeSpan Elapsed,
    int DeletedByDisk = 0)
{
    public int DeletedTotal => DeletedByAge + DeletedBySize + DeletedByDisk;
    public bool DidWork => DeletedTotal > 0;

    public static RSCRetentionReport Disabled(long bytes, long limit) =>
        new(0, 0, 0, bytes, bytes, limit, null, DateTimeOffset.UtcNow, TimeSpan.Zero);
}
