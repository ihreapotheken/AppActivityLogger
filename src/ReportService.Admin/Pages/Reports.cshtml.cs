using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Models;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Options;

namespace ReportService.Admin.Pages;

/// <summary>
/// Searchable, filterable, paginated listing. The handler binds a <see cref="RSAReportsFilterInput"/>,
/// hands it to <see cref="IRSAReportListingService"/>, and exposes the resulting view-model. The
/// SQLite-vs-disk fallback decision lives entirely in the service.
/// </summary>
public sealed class RSAReportsModel : PageModel
{
    private const int PageSize = 25;

    private readonly IRSAReportListingService _listing;
    private readonly RSCReportServiceOptions _options;

    public RSAReportsModel(IRSAReportListingService listing, RSCReportServiceOptions options)
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
        Listing = await _listing.ListAsync(Filter, PageSize, ct).ConfigureAwait(false);
    }
}
