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
    private static readonly RSAReportListingScope Scope = new(
        KindNotIn: new[] { "crash", "analytics" });

    private readonly IRSAReportListingService _listing;
    private readonly RSCReportServiceOptions _options;

    public RSAProblemReportsModel(IRSAReportListingService listing, RSCReportServiceOptions options)
    {
        _listing = listing;
        _options = options;
    }

    [BindProperty(SupportsGet = true)]
    public RSAReportsFilterInput Filter { get; set; } = new();

    public RSAReportsPageVM Listing { get; private set; } = default!;
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Listing = await _listing.ListAsync(Filter, PageSize, Scope, ct).ConfigureAwait(false);
    }
}
