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
    int MaxAgeDays);
