using Microsoft.AspNetCore.Http;
using ReportService.Admin.Models;

namespace ReportService.Admin.Services;

/// <summary>Outcome of a bulk delete: how many were removed, how many matched, and whether the
/// match set was capped (more matched than a single pass deletes).</summary>
public sealed record RSADeleteResult(int Deleted, int Matched, bool Truncated);

/// <summary>
/// Centralises operator-initiated report deletion (single + filtered-bulk) so every listing page
/// deletes through one audited, platform-canonicalising path instead of re-implementing it.
/// </summary>
public interface IRSAReportDeletionService
{
    /// <summary>Deletes one stored report (JSON + sibling attachment) and audits the action.</summary>
    Task<bool> DeleteOneAsync(string platform, string fileName, HttpContext ctx, CancellationToken ct);

    /// <summary>
    /// Deletes every report matching <paramref name="filter"/> (+ optional page <paramref name="scope"/>),
    /// bounded by an internal safety cap, and writes a single bulk-audit entry. Re-runs the listing
    /// query to resolve the match set, so the deletion matches exactly what the operator is looking at.
    /// </summary>
    Task<RSADeleteResult> DeleteMatchingAsync(RSAReportsFilterInput filter, RSAReportListingScope? scope, HttpContext ctx, CancellationToken ct);
}
