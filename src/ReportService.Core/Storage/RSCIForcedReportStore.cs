namespace ReportService.Storage;

/// <summary>
/// Read/write surface for the forced-report allow-list. The public ingestion service only ever
/// needs <see cref="ContainsAsync"/>; the admin app uses the rest to manage the list.
/// </summary>
public interface RSCIForcedReportStore
{
    /// <summary>Cheapest possible check — used by the public mobile-facing endpoint on every
    /// backend fetch, so it must avoid touching anything beyond a single row lookup.</summary>
    Task<bool> ContainsAsync(string id, CancellationToken ct);

    /// <summary>Upsert: adds the ID if absent, updates the note (and refreshes addedAt) if it
    /// already exists. Returns <c>true</c> when this call inserted a new row.</summary>
    Task<bool> AddAsync(string id, string? note, CancellationToken ct);

    /// <summary>Removes the ID. Returns <c>true</c> if a row was actually deleted.</summary>
    Task<bool> RemoveAsync(string id, CancellationToken ct);

    /// <summary>Lists every entry, newest first.</summary>
    Task<IReadOnlyList<RSCForcedReportEntry>> ListAsync(CancellationToken ct);
}
