using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;

namespace ReportService.Admin.Pages;

/// <summary>
/// Sink for the header tenant/app switcher. A POST sets the persistent <c>rsc_scope</c> cookie to the
/// chosen client/app and redirects back to the page the operator was on (303), so selecting a scope
/// keeps them where they are rather than bouncing to a fixed dashboard. The scope-fill middleware
/// then applies that cookie to every page. The switcher encodes its choice as <c>client|app</c>.
/// </summary>
public sealed class RSAScopeModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Analytics");

    public IActionResult OnPost([FromForm] string? sel, [FromForm] string? returnUrl)
    {
        string? client = null, app = null;
        if (!string.IsNullOrWhiteSpace(sel))
        {
            var parts = sel.Split('|');
            client = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
            app = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        }
        // Selecting a client/app resets the environment axis to "all" — env is a sub-filter chosen on
        // the dashboard itself, not part of the app switcher.
        RSAScopeCookie.Write(Response, client, app, env: null);

        // Only ever redirect within the app; fall back to the analytics home otherwise.
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : Redirect("/Analytics");
    }
}
