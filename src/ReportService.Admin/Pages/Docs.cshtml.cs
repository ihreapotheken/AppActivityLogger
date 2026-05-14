using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;

namespace ReportService.Admin.Pages;

/// <summary>
/// Renders the bundled repo <c>README.md</c> inside the admin shell at <c>/Documentation</c>.
/// </summary>
public sealed class RSADocsModel : PageModel
{
    private readonly IRSADocsService _docs;

    public RSADocsModel(IRSADocsService docs) => _docs = docs;

    public RSADocView? Readme { get; private set; }

    public IActionResult OnGet()
    {
        Readme = _docs.RenderReadme();
        return Readme is null ? NotFound() : Page();
    }
}
