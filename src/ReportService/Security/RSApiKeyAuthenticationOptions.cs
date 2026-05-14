using Microsoft.AspNetCore.Authentication;

namespace ReportService.Security;

public sealed class RSApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "apiKey";

    /// <summary>Server-side secret the handler compares incoming headers against. Empty → all requests fail.</summary>
    public string ExpectedKey { get; set; } = string.Empty;
}
