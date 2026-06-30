using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Models;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Options;

namespace ReportService.Admin.Pages;

/// <summary>
/// Listing scoped to user-initiated <c>Report a Problem</c> submissions — anything that isn't a
/// crash auto-upload (<c>kind == "crash"</c>) or an analytics event (<c>kind == "analytics"</c>).
/// The "include logs" toggle in the SDK is optional, so a real RaP can arrive with or without a
/// gzip attachment; the listing must show both. When an attachment is present and was shipped as
/// a plaintext JSON log dump (iOS today), the row's log-summary chip carries a per-level
/// histogram + http-event count parsed at ingest. Android attachments are AES-encrypted so they
/// render as "encrypted logcat — N bytes" instead.
/// </summary>
public sealed class RSAProblemReportsModel : PageModel
{
    private const int PageSize = 30;

    // Tenancy scope from the global client/app selection (filled from the rsc_scope cookie by the
    // scope-fill middleware, or an explicit ?client/?app). Null = all apps (operator-wide view).
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }

    private RSAReportListingScope Scope => new(
        KindNotIn: new[] { "crash", "analytics" },
        ClientId: string.IsNullOrWhiteSpace(Client) ? null : Client.Trim().ToLowerInvariant(),
        AppId: string.IsNullOrWhiteSpace(App) ? null : App.Trim().ToLowerInvariant());

    private readonly IRSAReportListingService _listing;
    private readonly RSCReportServiceOptions _options;
    private readonly IRSAReportDeletionService _deletion;

    public RSAProblemReportsModel(IRSAReportListingService listing, RSCReportServiceOptions options, IRSAReportDeletionService deletion)
    {
        _listing = listing;
        _options = options;
        _deletion = deletion;
    }

    [BindProperty(SupportsGet = true)]
    public RSAReportsFilterInput Filter { get; set; } = new();

    // Cap for the analytics aggregate. The headline total is always exact (TotalMatched); the
    // breakdown is computed from up to this many matched rows and flags Truncated beyond it.
    private const int SummaryCap = 2000;

    public RSAReportsPageVM Listing { get; private set; } = default!;
    public RSAReportsSummary Summary { get; private set; } = RSAReportsSummary.Empty;
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Listing = await _listing.ListAsync(Filter, PageSize, Scope, ct).ConfigureAwait(false);

        // Analytics over the whole filtered set (page 1, capped), aggregated in-memory so it stays
        // consistent with the active filter without a separate faceted query path.
        var matched = await _listing.ListAsync(Filter.WithPage(1), SummaryCap, Scope, ct).ConfigureAwait(false);
        Summary = RSAReportsSummary.From(matched.Items, matched.TotalMatched, SummaryCap);
    }

    public async Task<IActionResult> OnPostDeleteOneAsync(string platform, string fileName, CancellationToken ct)
    {
        var ok = await _deletion.DeleteOneAsync(platform, fileName, HttpContext, ct).ConfigureAwait(false);
        TempData["Flash"] = ok ? $"Deleted {fileName}." : $"Could not delete {fileName}.";
        return Redirect(Request.Path + Filter.ToQueryString(1));
    }

    public async Task<IActionResult> OnPostDeleteMatchingAsync(CancellationToken ct)
    {
        var res = await _deletion.DeleteMatchingAsync(Filter, Scope, HttpContext, ct).ConfigureAwait(false);
        TempData["Flash"] = res.Truncated
            ? $"Deleted {res.Deleted:N0} of {res.Matched:N0} matching reports — capped this pass, run again to continue."
            : $"Deleted {res.Deleted:N0} of {res.Matched:N0} matching reports.";
        return Redirect(Request.Path + Filter.ToQueryString(1));
    }
}
