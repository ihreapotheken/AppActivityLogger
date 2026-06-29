namespace ReportService.Storage;

/// <summary>
/// Maintenance-only surface on top of the plain <see cref="RSCIReportIndex"/>. These operations are
/// admin-triggered, can take noticeable time, and are expected to surface failures (unlike the
/// hot-path Upsert/List/Delete which degrade silently). Separating the interface keeps the normal
/// ingestion code path from depending on operational concerns.
/// </summary>
public interface RSCIReportIndexMaintenance
{
    /// <summary>Filtered + paginated listing. Counts + rows come from a single snapshot.</summary>
    Task<RSCReportPage> SearchAsync(RSCReportFilter filter, CancellationToken ct);

    /// <summary>Per-platform aggregates for the dashboard.</summary>
    Task<IReadOnlyList<RSCPlatformSummary>> SummarizeAsync(CancellationToken ct);

    /// <summary>
    /// Cumulative per-platform totals for every report deleted over the service's lifetime, folded
    /// in at deletion time. Survives the reports it describes, so the dashboard can show lifetime
    /// figures even after retention has purged the underlying data. Empty before the first deletion.
    /// </summary>
    Task<IReadOnlyList<RSCLifetimeReportStats>> GetLifetimeStatsAsync(CancellationToken ct);

    /// <summary>
    /// Aggregates for the admin Stats overview within a date window. Returns zero-filled daily
    /// volumes (split by ingestion channel for the volume chart) plus top-N buckets by device /
    /// pharmacy / app version / platform / channel. All counts come from a single connection so
    /// totals stay self-consistent.
    /// </summary>
    Task<RSCStatsReport> GetStatsAsync(DateTimeOffset from, DateTimeOffset until, int topN, CancellationToken ct);

    /// <summary>Current status snapshot (DB path, size, schema version, last-integrity, drift, etc.).</summary>
    Task<RSCIndexStatusReport> GetStatusAsync(CancellationToken ct);

    /// <summary>Reconcile the index with the on-disk file system. Both directions.</summary>
    Task<RSCRebuildReport> RebuildAsync(RSCIReportStore fileStore, IReadOnlyList<string> platforms, CancellationToken ct);

    /// <summary>Reject pragma integrity check. <c>"ok"</c> on success, otherwise the first error row.</summary>
    Task<string> IntegrityCheckAsync(CancellationToken ct);

    /// <summary>VACUUM + ANALYZE, atomic.</summary>
    Task VacuumAsync(CancellationToken ct);

    /// <summary>Writes an atomic, verified backup of the live DB to <paramref name="destinationPath"/>.</summary>
    Task BackupAsync(string destinationPath, CancellationToken ct);
}
