using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReportService.Admin.Pages;

/// <summary>
/// Signs the operator out. Accepts POST only (to be CSRF-safe via the antiforgery token embedded in
/// the layout's sign-out form) and redirects back to the login page.
/// </summary>
public sealed class RSALogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }

    public IActionResult OnGet() => RedirectToPage("/Index");
}
