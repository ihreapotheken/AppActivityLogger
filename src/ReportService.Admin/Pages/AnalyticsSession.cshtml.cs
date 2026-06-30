using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsSessionModel : PageModel
{
    private readonly RSCIAnalyticsStore _store;

    public RSAAnalyticsSessionModel(RSCIAnalyticsStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string Platform { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "env")] public string? Env { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public IReadOnlyList<RSCAnalyticsStoredEvent> Timeline { get; private set; } = Array.Empty<RSCAnalyticsStoredEvent>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Platform) || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/AnalyticsSessions");
        }
        // A session is keyed by the full tenant + platform; pass the scope carried in the link.
        var scope = RSATenantScopes.Build(App, Env, Client, Platform);
        Timeline = await _store.GetSessionTimelineAsync(scope, Id, ct).ConfigureAwait(false);
        return Page();
    }
}
