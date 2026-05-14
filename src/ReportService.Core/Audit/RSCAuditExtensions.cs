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
        return log.RecordAsync(entry, ctx.RequestAborted);
    }
}
