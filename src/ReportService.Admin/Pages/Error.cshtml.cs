using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReportService.Admin.Pages;

[AllowAnonymous]
public sealed class RSAErrorModel : PageModel
{
    public void OnGet() { }
}
