namespace ReportService.Storage.Retention;

/// <summary>
/// Snapshot of the store's storage usage. Read by the admin Status / Dashboard pages so an
/// operator can see headroom without triggering a delete.
/// </summary>
public sealed record RSCRetentionStats(
    long UsedBytes,
    long LimitBytes,
    int ReportCount,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest,
    bool Enabled,
    int MaxAgeDays,
    long? DiskTotalBytes = null,
    long? DiskFreeBytes = null)
{
    /// <summary>Percent of the underlying filesystem in use, or null if disk space is unknown.</summary>
    public double? DiskUsedPercent =>
        DiskTotalBytes is > 0 && DiskFreeBytes is { } free
            ? (DiskTotalBytes.Value - free) * 100.0 / DiskTotalBytes.Value
            : null;
}
