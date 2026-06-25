using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Analytics;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsSessionModel : PageModel
{
    private readonly RSCIAnalyticsStore _store;

    public RSAAnalyticsSessionModel(RSCIAnalyticsStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string Platform { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = string.Empty;

    public IReadOnlyList<RSCAnalyticsStoredEvent> Timeline { get; private set; } = Array.Empty<RSCAnalyticsStoredEvent>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Platform) || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/AnalyticsSessions");
        }
        Timeline = await _store.GetSessionTimelineAsync(Platform, Id, ct).ConfigureAwait(false);
        return Page();
    }
}
