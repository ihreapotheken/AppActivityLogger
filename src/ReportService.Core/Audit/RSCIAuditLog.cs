namespace ReportService.Audit;

/// <summary>
/// Persisted audit log for admin actions: login success/failure, delete, rebuild, vacuum, backup,
/// integrity check, export. Writes are best-effort (never throw into the caller) so a broken
/// audit DB never stops an operator from doing their job — but the loss is logged.
/// </summary>
public interface RSCIAuditLog
{
    Task RecordAsync(RSCAuditEntry entry, CancellationToken ct);
    Task<IReadOnlyList<RSCAuditEntry>> RecentAsync(int limit, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
