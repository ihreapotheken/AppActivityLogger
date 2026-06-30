using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Models;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsModel : PageModel
{
    private const int PageSize = 25;
    private static readonly string[] AllowedPlatforms = { "ios", "android" };
    private static readonly RSAReportListingScope Scope = new(KindIn: new[] { "analytics" });

    private readonly IRSAAnalyticsDashboardService _analytics;
    private readonly IRSAReportListingService _listing;
    private readonly IRSAReportDeletionService _deletion;
    private readonly RSCReportServiceOptions _options;
    private readonly RSCICatalog _catalog;

    public RSAAnalyticsModel(
        IRSAAnalyticsDashboardService analytics,
        IRSAReportListingService listing,
        IRSAReportDeletionService deletion,
        RSCReportServiceOptions options,
        RSCICatalog catalog)
    {
        _analytics = analytics;
        _listing = listing;
        _deletion = deletion;
        _options = options;
        _catalog = catalog;
    }

    [BindProperty(SupportsGet = true, Name = "platform")]
    public string? Platform { get; set; }

    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "env")] public string? Env { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    [BindProperty(SupportsGet = true)]
    public RSAReportsFilterInput Filter { get; set; } = new();

    public RSAAnalyticsDashboardVM Dashboard { get; private set; } = default!;
    public RSAReportsPageVM Listing { get; private set; } = default!;
    public RSATenantScopeVM TenantScope { get; private set; } = default!;

    public string? CanonicalPlatform =>
        Platform is { Length: > 0 } p && Array.IndexOf(AllowedPlatforms, p.ToLowerInvariant()) >= 0
            ? p.ToLowerInvariant()
            : null;

    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var scope = RSATenantScopes.Build(App, Env, Client, CanonicalPlatform);
        TenantScope = await RSATenantScopes.BuildVmAsync(_catalog, "/Analytics", App, Env, Client, CanonicalPlatform, ct).ConfigureAwait(false);
        Dashboard = await _analytics.BuildAsync(scope, ct).ConfigureAwait(false);
        // The scope-tab platform (URL ?platform=) and the bound filter share one query field.
        // Razor's binding gives Filter.Platform the same string, so the listing already inherits
        // the scope. No extra wiring needed.
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
}
