using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

/// <summary>
/// <c>/AnalyticsSales</c> — the revenue / commerce view: OTC purchases rolled into revenue, orders,
/// average order value, a daily trend, payment + shipping breakdowns, top products, and prescription
/// activity. Sibling to <c>/Analytics</c> (engagement), backed by <see cref="IRSAAnalyticsSalesService"/>.
/// </summary>
public sealed class RSAAnalyticsSalesModel : PageModel
{
    private static readonly string[] AllowedPlatforms = { "ios", "android" };

    private readonly IRSAAnalyticsSalesService _sales;

    public RSAAnalyticsSalesModel(IRSAAnalyticsSalesService sales)
    {
        _sales = sales;
    }

    [BindProperty(SupportsGet = true, Name = "platform")]
    public string? Platform { get; set; }

    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public RSAAnalyticsSalesVM Sales { get; private set; } = default!;

    public string? CanonicalPlatform =>
        Platform is { Length: > 0 } p && Array.IndexOf(AllowedPlatforms, p.ToLowerInvariant()) >= 0
            ? p.ToLowerInvariant()
            : null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var scope = RSATenantScopes.Build(App, Client, CanonicalPlatform);
        Sales = await _sales.BuildAsync(scope, ct).ConfigureAwait(false);
    }
}
