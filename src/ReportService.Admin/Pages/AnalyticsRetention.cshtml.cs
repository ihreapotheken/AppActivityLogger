using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsRetentionModel : PageModel
{
    private const int CohortWindowDays = 60;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAAnalyticsRetentionModel(RSCIAnalyticsStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    [BindProperty(SupportsGet = true)] public string? Platform { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public RSCAnalyticsRetentionSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<RSCAnalyticsRetentionCohortRow> Cohorts { get; private set; } = Array.Empty<RSCAnalyticsRetentionCohortRow>();
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;
    public int WindowDays => CohortWindowDays;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var scope = RSATenantScopes.Build(App, Client, Platform);
        Summary = await _store.GetRetentionSummaryAsync(scope, CohortWindowDays, ct).ConfigureAwait(false);
        Cohorts = await _store.ListRetentionCohortsAsync(scope, CohortWindowDays, ct).ConfigureAwait(false);
    }
}
