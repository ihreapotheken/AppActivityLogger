using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Audit;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage.ApiKeys;

namespace ReportService.Admin.Pages;

/// <summary>
/// Client self-service sign-in. A client pastes its API access key; if the key resolves and is bound
/// to a client, we sign in a cookie principal carrying the <see cref="RSCTenantClaims.ClientId"/>
/// claim. The pipeline's client-scope middleware then confines that session to its own per-app
/// dashboards (see <c>RSAClientLoginScope</c>). An unbound (root/operator) key is rejected — those
/// sign in on the operator <c>/Login</c> page instead. Brute force is throttled per source IP.
/// </summary>
[AllowAnonymous]
public sealed class RSAClientLoginModel : PageModel
{
    private readonly RSCReportServiceOptions _options;
    private readonly RSCIApiKeyStore _keys;
    private readonly RSCIAuthAbuseTracker _abuse;
    private readonly RSCIAuditLog _audit;
    private readonly ILogger<RSAClientLoginModel> _logger;

    public RSAClientLoginModel(
        RSCReportServiceOptions options, RSCIApiKeyStore keys,
        RSCIAuthAbuseTracker abuse, RSCIAuditLog audit, ILogger<RSAClientLoginModel> logger)
    {
        _options = options;
        _keys = keys;
        _abuse = abuse;
        _audit = audit;
        _logger = logger;
    }

    [BindProperty]
    public string Key { get; set; } = string.Empty;

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        // An existing client session goes straight to its dashboards.
        if (User?.FindFirst(RSCTenantClaims.ClientId)?.Value is { Length: > 0 })
            return RedirectToPage("/Analytics");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var source = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var decision = await _abuse.CheckAsync(source, HttpContext.RequestAborted);
        if (decision.IsBanned)
        {
            Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
            Error = "Too many failed attempts. Try again later.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Key))
        {
            Error = "Enter your access key.";
            return Page();
        }

        var resolution = RSCApiKeyResolver.Resolve(Key, _options, _keys);
        if (resolution is null || string.IsNullOrEmpty(resolution.ClientId))
        {
            await _abuse.RecordFailureAsync(source, HttpContext.RequestAborted);
            await _audit.RecordAsync(HttpContext, "client.login", success: false,
                details: resolution is null ? "invalid key" : "key not bound to a client");
            _logger.LogWarning("Client login failed from {Remote}", source);
            Error = "That key is not a client access key.";
            return Page();
        }

        await _abuse.ClearAsync(source, HttpContext.RequestAborted);

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, resolution.ClientId),
                new Claim(RSCTenantClaims.ClientId, resolution.ClientId),
            },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        await _audit.RecordAsync(HttpContext, "client.login", success: true, target: resolution.ClientId);
        _logger.LogInformation("Client login succeeded for {Client} from {Remote}", resolution.ClientId, source);
        return RedirectToPage("/Analytics");
    }
}
