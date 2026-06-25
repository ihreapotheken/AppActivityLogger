using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Analytics;
using ReportService.Options;

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

    public RSCAnalyticsRetentionSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<RSCAnalyticsRetentionCohortRow> Cohorts { get; private set; } = Array.Empty<RSCAnalyticsRetentionCohortRow>();
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;
    public int WindowDays => CohortWindowDays;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Summary = await _store.GetRetentionSummaryAsync(Platform, CohortWindowDays, ct).ConfigureAwait(false);
        Cohorts = await _store.ListRetentionCohortsAsync(Platform, CohortWindowDays, ct).ConfigureAwait(false);
    }
}
