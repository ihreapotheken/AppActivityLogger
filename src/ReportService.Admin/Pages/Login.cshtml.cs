using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Options;
using ReportService.Audit;
using ReportService.Security;

namespace ReportService.Admin.Pages;

/// <summary>
/// Operator sign-in page. Performs a constant-time compare of the submitted admin key against
/// <see cref="RSAAdminOptions.AdminKey"/>; on success, signs in the cookie scheme so subsequent pages
/// can enforce <see cref="AuthorizeAttribute"/>. Brute-force attempts from the same source IP are
/// throttled by the persisted <see cref="RSCIAuthAbuseTracker"/>.
/// </summary>
[AllowAnonymous]
public sealed class RSALoginModel : PageModel
{
    private readonly RSAAdminOptions _options;
    private readonly RSCIAuthAbuseTracker _abuse;
    private readonly RSCIAuditLog _audit;
    private readonly ILogger<RSALoginModel> _logger;

    public RSALoginModel(RSAAdminOptions options, RSCIAuthAbuseTracker abuse, RSCIAuditLog audit, ILogger<RSALoginModel> logger)
    {
        _options = options;
        _abuse = abuse;
        _audit = audit;
        _logger = logger;
    }

    [BindProperty]
    public string Key { get; set; } = string.Empty;

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        var source = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var decision = await _abuse.CheckAsync(source, HttpContext.RequestAborted);
        if (decision.IsBanned)
        {
            Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
            _logger.LogWarning("Admin login rejected: source {Source} is banned for {Seconds}s", source, decision.RetryAfterSeconds);
            Error = "Too many failed attempts. Try again later.";
            return Page();
        }

        if (string.IsNullOrEmpty(_options.AdminKey))
        {
            _logger.LogWarning("Admin login rejected: AdminKey is not configured on the server");
            Error = "Admin key is not configured.";
            return Page();
        }

        if (string.IsNullOrEmpty(Key))
        {
            Error = "Enter the admin key.";
            return Page();
        }

        if (!RSCSecretComparer.Matches(Key, _options.AdminKey))
        {
            await _abuse.RecordFailureAsync(source, HttpContext.RequestAborted);
            await _audit.RecordAsync(HttpContext, "admin.login", success: false, details: "invalid key");
            _logger.LogWarning("Admin login failed from {Remote}", source);
            Error = "Invalid admin key.";
            return Page();
        }

        await _abuse.ClearAsync(source, HttpContext.RequestAborted);

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "operator") },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal);

        await _audit.RecordAsync(HttpContext, "admin.login", success: true);
        _logger.LogInformation("Admin login succeeded from {Remote}", HttpContext.Connection.RemoteIpAddress);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }
        return RedirectToPage("/Index");
    }
}
