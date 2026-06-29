using Microsoft.AspNetCore.Http;

namespace ReportService.Audit;

public static class RSCAuditExtensions
{
    public static Task RecordAsync(
        this RSCIAuditLog log,
        HttpContext ctx,
        string action,
        bool success,
        string? target = null,
        string? details = null)
    {
        var entry = new RSCAuditEntry(
            At: DateTimeOffset.UtcNow,
            Actor: ctx.User?.Identity?.Name ?? "anonymous",
            RemoteAddress: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Action: action,
            Target: target,
            Details: details,
            Success: success);
        // Deliberately NOT ctx.RequestAborted. Audit rows for destructive actions are written
        // after the action has already completed, so if the operator's browser disconnects the
        // request token is already cancelled and the audit insert would be dropped — leaving a
        // wipe/restore/rotate with no trace. The audit log must outlive the request lifetime, so
        // persistence runs under CancellationToken.None (the store still bounds the write with its
        // own SQLite command timeout).
        return log.RecordAsync(entry, CancellationToken.None);
    }
}
