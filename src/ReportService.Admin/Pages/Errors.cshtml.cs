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
    // The listing covers the full fault population the dashboard tiles count — fatal crashes plus
    // non-fatal Kind="error" reports. The Kind dropdown narrows to one or the other; "all" shows both.
    // Tenancy scope from the global client/app selection (rsc_scope cookie / explicit ?client/?app);
    // null = all apps. Applies to the listing AND "delete matching" so deletes can't cross app bounds.
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }

    private RSAReportListingScope Scope => new(
        KindIn: new[] { "crash", "error" },
        ClientId: string.IsNullOrWhiteSpace(Client) ? null : Client.Trim().ToLowerInvariant(),
        AppId: string.IsNullOrWhiteSpace(App) ? null : App.Trim().ToLowerInvariant());

    private readonly IRSAErrorDashboardService _errors;
    private readonly IRSAReportListingService _listing;
    private readonly IRSAReportDeletionService _deletion;
    private readonly RSCReportServiceOptions _options;

    public RSAErrorsModel(
        IRSAErrorDashboardService errors,
        IRSAReportListingService listing,
        IRSAReportDeletionService deletion,
        RSCReportServiceOptions options)
    {
        _errors = errors;
        _listing = listing;
        _deletion = deletion;
        _options = options;
    }

    [BindProperty(SupportsGet = true, Name = "platform")]
    public string? Platform { get; set; }

    [BindProperty(SupportsGet = true)]
    public RSAReportsFilterInput Filter { get; set; } = new();

    // Error-rate chart range. `range` is a preset key (7d/30d/3m/6m/1y/custom); rateFrom/rateTo are
    // the inclusive custom bounds (only read when range=custom). Named distinctly from the listing
    // filter's from/until so the two controls don't collide on the query string.
    [BindProperty(SupportsGet = true, Name = "range")]
    public string? RangeKey { get; set; }

    [BindProperty(SupportsGet = true, Name = "rateFrom")]
    public DateOnly? RateFrom { get; set; }

    [BindProperty(SupportsGet = true, Name = "rateTo")]
    public DateOnly? RateTo { get; set; }

    public RSAErrorRateRange SelectedRange { get; private set; }
    public RSAErrorRateWindow RateWindow { get; private set; } = default!;

    public RSAErrorDashboardVM Dashboard { get; private set; } = default!;
    public RSAReportsPageVM Listing { get; private set; } = default!;

    /// <summary>Human-readable caption for the selected range, used in the chart heading + aria label.</summary>
    public string RangeLabel => SelectedRange switch
    {
        RSAErrorRateRange.Last30Days => "last 30 days",
        RSAErrorRateRange.Last3Months => "last 3 months",
        RSAErrorRateRange.Last6Months => "last 6 months",
        RSAErrorRateRange.LastYear => "last year",
        RSAErrorRateRange.Custom => $"{RateWindow.FromUtc:yyyy-MM-dd} → {RateWindow.ToUtc.AddDays(-1):yyyy-MM-dd}",
        _ => "last 7 days",
    };

    public string? CanonicalPlatform =>
        Platform is { Length: > 0 } p && Array.IndexOf(AllowedPlatforms, p.ToLowerInvariant()) >= 0
            ? p.ToLowerInvariant()
            : null;

    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;

    public async Task OnGetAsync(CancellationToken ct)
    {
        SelectedRange = ParseRange(RangeKey);
        RateWindow = RSAErrorRateWindow.Resolve(SelectedRange, RateFrom, RateTo, DateTimeOffset.UtcNow);
        Dashboard = _errors.Build(CanonicalPlatform, RateWindow);
        Listing = await _listing.ListAsync(Filter, PageSize, Scope, ct).ConfigureAwait(false);
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

    private static RSAErrorRateRange ParseRange(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "30d" => RSAErrorRateRange.Last30Days,
        "3m" => RSAErrorRateRange.Last3Months,
        "6m" => RSAErrorRateRange.Last6Months,
        "1y" => RSAErrorRateRange.LastYear,
        "custom" => RSAErrorRateRange.Custom,
        _ => RSAErrorRateRange.Last7Days,
    };
}
