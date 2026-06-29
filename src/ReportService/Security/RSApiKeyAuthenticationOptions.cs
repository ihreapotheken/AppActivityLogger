using Microsoft.AspNetCore.Authentication;

namespace ReportService.Security;

public sealed class RSApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "apiKey";

    // The handler resolves keys through RSCApiKeyResolver (static root key + managed DB keys), so no
    // per-scheme secret lives here anymore.
}
