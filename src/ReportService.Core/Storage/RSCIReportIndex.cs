namespace ReportService.Storage;

/// <summary>
/// Metadata-only sidecar index over an <see cref="RSCIReportStore"/>. Accelerates listing without
/// duplicating the JSON body / attachment. Implementations must be safe under concurrent callers.
/// </summary>
public interface RSCIReportIndex
{
    /// <summary>Upsert keyed on <c>(platform, file_name)</c>.</summary>
    Task UpsertAsync(RSCReportMetadata metadata, CancellationToken ct);

    /// <summary>Lists all rows for a platform, newest first.</summary>
    Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, CancellationToken ct);

    /// <summary>Paginated variant: at most <paramref name="limit"/> rows (≤0 = unlimited), skipping <paramref name="offset"/>.</summary>
    Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, int limit, int offset, CancellationToken ct);

    /// <summary>Deletes the row if present. True when a row was actually removed.</summary>
    Task<bool> DeleteAsync(string platform, string fileName, CancellationToken ct);

    /// <summary>
    /// Folds the row's count + byte footprint into the persistent lifetime-statistics rollup, then
    /// deletes it — atomically, in one transaction. The real deletion paths (operator delete, bulk
    /// delete, retention sweep) call this so a report's contribution survives in lifetime totals
    /// before its metadata row is destroyed. Plain <see cref="DeleteAsync"/> is the
    /// drift-reconciliation delete and deliberately does <em>not</em> accumulate (a rebuild pruning
    /// a stale row must not inflate the lifetime counters). True when a row was actually removed.
    /// </summary>
    Task<bool> RecordLifetimeAndDeleteAsync(string platform, string fileName, CancellationToken ct);
}
