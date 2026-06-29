using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;

namespace ReportService.Admin.Pages;

/// <summary>
/// Renders the bundled repo docs (README + guide chapters) as a tabbed, navigable page inside the
/// admin shell at <c>/Documentation</c>.
/// </summary>
public sealed class RSADocsModel : PageModel
{
    private readonly IRSADocsService _docs;

    public RSADocsModel(IRSADocsService docs) => _docs = docs;

    public RSADocSet? Docs { get; private set; }

    public IActionResult OnGet()
    {
        Docs = _docs.RenderDocs();
        return Docs is null ? NotFound() : Page();
    }
}
