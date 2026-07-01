using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsSessionsModel : PageModel
{
    private const int PageSize = 50;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAAnalyticsSessionsModel(RSCIAnalyticsStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    [BindProperty(SupportsGet = true)] public string? Platform { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public IReadOnlyList<RSCAnalyticsSessionRow> Rows { get; private set; } = Array.Empty<RSCAnalyticsSessionRow>();
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var scope = RSATenantScopes.Build(App, Client, Platform);
        var offset = Math.Max(0, (Math.Max(1, PageNumber) - 1) * PageSize);
        Rows = await _store.ListSessionsAsync(scope, PageSize, offset, ct).ConfigureAwait(false);
    }

    public string BuildPageHref(int page)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(App)) parts.Add($"app={Uri.EscapeDataString(App)}");
        if (!string.IsNullOrEmpty(Client)) parts.Add($"client={Uri.EscapeDataString(Client)}");
        if (!string.IsNullOrEmpty(Platform)) parts.Add($"platform={Uri.EscapeDataString(Platform)}");
        if (page > 1) parts.Add($"page={page}");
        return parts.Count == 0 ? "?" : "?" + string.Join('&', parts);
    }
}
