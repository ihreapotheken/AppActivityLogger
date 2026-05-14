using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReportService.Admin.Pages;

/// <summary>
/// Hosts the OpenAPI explorer inside the admin shell. The actual Swagger UI bundle is served
/// by <c>UseSwaggerUI</c> at <c>/docs/</c> (its own self-contained HTML); this page just wraps
/// that with the standard sidebar + page header so the docs are reachable without leaving the
/// admin chrome.
/// </summary>
public sealed class RSAApiDocsModel : PageModel
{
    public void OnGet() { }
}
