using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Models;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Options;

namespace ReportService.Admin.Pages;

public sealed class RSAErrorsModel : PageModel
{
    private const int PageSize = 25;
    private static readonly string[] AllowedPlatforms = { "ios", "android" };
    private static readonly RSAReportListingScope Scope = new(KindIn: new[] { "crash" });

    private readonly IRSAErrorDashboardService _errors;
    private readonly IRSAReportListingService _listing;
    private readonly RSCReportServiceOptions _options;

    public RSAErrorsModel(
        IRSAErrorDashboardService errors,
        IRSAReportListingService listing,
        RSCReportServiceOptions options)
    {
        _errors = errors;
        _listing = listing;
        _options = options;
    }

    [BindProperty(SupportsGet = true, Name = "platform")]
    public string? Platform { get; set; }

    [BindProperty(SupportsGet = true)]
    public RSAReportsFilterInput Filter { get; set; } = new();

    public RSAErrorDashboardVM Dashboard { get; private set; } = default!;
    public RSAReportsPageVM Listing { get; private set; } = default!;

    public string? CanonicalPlatform =>
        Platform is { Length: > 0 } p && Array.IndexOf(AllowedPlatforms, p.ToLowerInvariant()) >= 0
            ? p.ToLowerInvariant()
            : null;

    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = _errors.Build(CanonicalPlatform);
        Listing = await _listing.ListAsync(Filter, PageSize, Scope, ct).ConfigureAwait(false);
    }
}
