using System.Text;
using Microsoft.AspNetCore.Http;
using ReportService.Admin.Models;
using ReportService.Audit;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Validation;

namespace ReportService.Admin.Services;

/// <summary>
/// Report-store-backed deletion. Single deletes go straight through <see cref="RSCIReportStore.Delete"/>;
/// bulk deletes re-run the listing query (so the deleted set matches exactly what the operator filtered),
/// remove each match, and write one audit row. Every path is audited and platform-canonicalised.
/// </summary>
public sealed class RSAReportDeletionService : IRSAReportDeletionService
{
    /// <summary>Upper bound on a single bulk pass, so one click can't walk the whole store unbounded.
    /// When more match than this, the pass deletes the cap and reports <c>Truncated</c>; re-running
    /// continues from the next page of matches.</summary>
    private const int DeleteCap = 5000;

    private readonly RSCIReportStore _store;
    private readonly IRSAReportListingService _listing;
    private readonly RSCIAuditLog _audit;
    private readonly RSCReportServiceOptions _options;
    private readonly ILogger<RSAReportDeletionService> _log;

    public RSAReportDeletionService(
        RSCIReportStore store,
        IRSAReportListingService listing,
        RSCIAuditLog audit,
        RSCReportServiceOptions options,
        ILogger<RSAReportDeletionService> log)
    {
        _store = store;
        _listing = listing;
        _audit = audit;
        _options = options;
        _log = log;
    }

    public async Task<bool> DeleteOneAsync(string platform, string fileName, HttpContext ctx, CancellationToken ct)
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p)
        {
            await _audit.RecordAsync(ctx, "report.delete", success: false, target: $"{platform}/{fileName}", details: "unknown platform");
            return false;
        }

        var removed = _store.Delete(p, fileName);
        await _audit.RecordAsync(ctx, "report.delete", success: removed, target: $"{p}/{fileName}");
        if (removed)
        {
            _log.LogInformation("Operator {Operator} deleted {Platform}/{File} from {Remote}",
                ctx.User?.Identity?.Name, p, fileName, ctx.Connection.RemoteIpAddress);
        }
        else
        {
            _log.LogWarning("Operator {Operator} attempted to delete missing {Platform}/{File}",
                ctx.User?.Identity?.Name, p, fileName);
        }
        return removed;
    }

    public async Task<RSADeleteResult> DeleteMatchingAsync(RSAReportsFilterInput filter, RSAReportListingScope? scope, HttpContext ctx, CancellationToken ct)
    {
        // Resolve the match set through the same listing path the page renders, so "delete matching"
        // removes precisely the rows the operator is filtering — never more.
        var page = await _listing
            .ListAsync(filter.WithPage(1), DeleteCap, scope ?? new RSAReportListingScope(), ct)
            .ConfigureAwait(false);

        var deleted = 0;
        foreach (var row in page.Items)
        {
            if (_store.Delete(row.Platform, row.FileName)) deleted++;
        }

        var truncated = page.TotalMatched > page.Items.Count;
        var desc = DescribeFilter(filter, scope);
        await _audit.RecordAsync(ctx, "report.delete-bulk", success: deleted > 0,
            target: desc,
            details: $"deleted {deleted} of {page.TotalMatched} matched{(truncated ? $"; capped at {DeleteCap} per pass" : "")}");
        _log.LogWarning("Operator {Operator} bulk-deleted {Deleted}/{Matched} reports from {Remote} (filter: {Filter})",
            ctx.User?.Identity?.Name, deleted, page.TotalMatched, ctx.Connection.RemoteIpAddress, desc);

        return new RSADeleteResult(deleted, page.TotalMatched, truncated);
    }

    /// <summary>Compact, audit-friendly summary of the active filter + page scope.</summary>
    private static string DescribeFilter(RSAReportsFilterInput f, RSAReportListingScope? scope)
    {
        var parts = new List<string>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) parts.Add($"{k}={v}"); }
        Add("platform", f.Platform);
        Add("q", f.Q);
        Add("pharmacyId", f.PharmacyId);
        Add("userId", f.UserId);
        Add("email", f.Email);
        Add("phone", f.Phone);
        Add("appVersion", f.AppVersion);
        Add("channel", f.Channel);
        Add("topFrame", f.TopFrame);
        if (f.HasAttachment is { } ha) Add("hasAttachment", ha ? "true" : "false");
        if (f.From is { } from) Add("from", from.ToString("yyyy-MM-ddTHH:mm"));
        if (f.Until is { } until) Add("until", until.ToString("yyyy-MM-ddTHH:mm"));
        if (scope?.KindIn is { Count: > 0 } ki) Add("kindIn", string.Join('|', ki));
        if (scope?.KindNotIn is { Count: > 0 } kn) Add("kindNotIn", string.Join('|', kn));
        if (scope?.RequireAttachment is true) parts.Add("requireAttachment");
        return parts.Count == 0 ? "(all reports)" : string.Join("; ", parts);
    }
}
