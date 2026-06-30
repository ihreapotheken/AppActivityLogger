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
    private readonly RSCICatalog _catalog;

    public RSAAnalyticsRetentionModel(RSCIAnalyticsStore store, RSCReportServiceOptions options, RSCICatalog catalog)
    {
        _store = store;
        _options = options;
        _catalog = catalog;
    }

    [BindProperty(SupportsGet = true)] public string? Platform { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "env")] public string? Env { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public RSCAnalyticsRetentionSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<RSCAnalyticsRetentionCohortRow> Cohorts { get; private set; } = Array.Empty<RSCAnalyticsRetentionCohortRow>();
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;
    public RSATenantScopeVM TenantScope { get; private set; } = default!;
    public int WindowDays => CohortWindowDays;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var scope = RSATenantScopes.Build(App, Env, Client, Platform);
        TenantScope = await RSATenantScopes.BuildVmAsync(_catalog, "/AnalyticsRetention", App, Env, Client, Platform, ct).ConfigureAwait(false);
        Summary = await _store.GetRetentionSummaryAsync(scope, CohortWindowDays, ct).ConfigureAwait(false);
        Cohorts = await _store.ListRetentionCohortsAsync(scope, CohortWindowDays, ct).ConfigureAwait(false);
    }
}
