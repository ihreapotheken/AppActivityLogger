using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReportService.Admin.Pages;

/// <summary>
/// Landing page shown when an operator navigates to an optional "Submissions" feature area that was
/// compiled out via a build flag (see <see cref="global::ReportService.RSCFeatureFlags"/>). The
/// <see cref="global::ReportService.Admin.Services.RSAFeatureGateFilter"/> redirects here with
/// <c>?feature=…</c>.
/// </summary>
public sealed class RSAFeatureUnavailableModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Feature { get; set; }

    public string FeatureName => Feature?.ToLowerInvariant() switch
    {
        "analytics" => "Analytics",
        "problemreports" or "problem-reports" or "reports" => "Problem reports",
        _ => "This feature",
    };
}
