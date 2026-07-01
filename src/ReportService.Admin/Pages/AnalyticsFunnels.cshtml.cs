using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsFunnelsModel : PageModel
{
    private const int LookbackDays = 30;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAAnalyticsFunnelsModel(RSCIAnalyticsStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    [BindProperty(SupportsGet = true, Name = "funnel")] public string? FunnelKey { get; set; }
    [BindProperty(SupportsGet = true)]                  public string? Platform  { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")]    public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public IReadOnlyList<RSCAnalyticsFunnelDefinition> Definitions { get; private set; } = Array.Empty<RSCAnalyticsFunnelDefinition>();
    public RSCAnalyticsFunnelDefinition? Selected { get; private set; }
    public IReadOnlyList<RSCAnalyticsFunnelStepStat> Stats { get; private set; } = Array.Empty<RSCAnalyticsFunnelStepStat>();
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;
    public int Window => LookbackDays;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Definitions = await _store.ListFunnelDefinitionsAsync(onlyEnabled: false, ct).ConfigureAwait(false);

        if (Definitions.Count == 0) return;

        // Default to the first definition when the operator hasn't picked one — first paint of
        // the page is more useful than an empty body.
        var key = string.IsNullOrWhiteSpace(FunnelKey) ? Definitions[0].FunnelKey : FunnelKey;
        Selected = Definitions.FirstOrDefault(d => string.Equals(d.FunnelKey, key, StringComparison.Ordinal));
        if (Selected is null) return;

        var scope = RSATenantScopes.Build(App, Client, Platform);
        var until = DateTimeOffset.UtcNow.AddDays(1);
        var from = until.AddDays(-LookbackDays - 1);
        var raw = await _store.GetFunnelSummaryAsync(Selected.FunnelKey, from, until, scope, ct).ConfigureAwait(false);

        // Pad the result with zero rows for steps the matcher didn't reach inside the window so
        // the table always renders one row per defined step.
        var byIdx = raw.ToDictionary(r => r.StepIndex, r => r.SessionsReached);
        var padded = new List<RSCAnalyticsFunnelStepStat>();
        for (var i = 0; i < Selected.Steps.Count; i++)
        {
            padded.Add(new RSCAnalyticsFunnelStepStat(
                StepIndex: i,
                StepName: Selected.Steps[i].Name,
                SessionsReached: byIdx.TryGetValue(i, out var v) ? v : 0));
        }
        Stats = padded;
    }
}
