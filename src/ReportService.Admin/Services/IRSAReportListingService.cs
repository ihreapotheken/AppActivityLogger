using ReportService.Admin.Models;
using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>
/// Read-side facade for the Reports page. The page model passes a bound <see cref="RSAReportsFilterInput"/>
/// and gets back a fully-shaped page view-model — no SQLite/disk fallback ladder leaks into the
/// presentation layer.
/// </summary>
public interface IRSAReportListingService
{
    /// <summary>
    /// Used by the per-category pages (Analytics, ProblemReports, Errors). The
    /// <paramref name="scope"/> applies invisibly on top of the user's bound filter — pages set it
    /// to the kind/attachment constraints that define their listing scope. Filters surfaced in the
    /// UI flow through <paramref name="filter"/> as usual.
    /// </summary>
    Task<RSAReportsPageVM> ListAsync(RSAReportsFilterInput filter, int pageSize, RSAReportListingScope scope, CancellationToken ct);
}

/// <summary>
/// Implicit page-level constraints layered on top of the user's filter. <c>KindIn</c> / <c>KindNotIn</c>
/// are mutually exclusive in practice but both pass through to the index. <c>RequireAttachment</c>
/// forces <c>has_attachment = 1</c> regardless of what the form sent.
/// </summary>
public sealed record RSAReportListingScope(
    IReadOnlyList<string>? KindIn = null,
    IReadOnlyList<string>? KindNotIn = null,
    bool? RequireAttachment = null,
    // Tenancy scope (database-per-app, FileSystem mode): restrict the listing to one client/app.
    // Null = all apps (the operator-wide view). The fan-out report store stamps each row's owning
    // (client, app), which the listing service filters on.
    string? ClientId = null,
    string? AppId = null);
