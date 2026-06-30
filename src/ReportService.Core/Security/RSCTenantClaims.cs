namespace ReportService.Security;

/// <summary>
/// Claim types that carry tenancy on an authenticated principal. The client an API key (or a client
/// cookie login) is bound to travels as <see cref="ClientId"/>; the ingestion path reads it to
/// attribute a batch to its client, and the admin app reads it to scope a client login's dashboards.
/// </summary>
public static class RSCTenantClaims
{
    /// <summary>Catalog client slug the authenticated identity is bound to. Absent on root/unbound
    /// operator keys (which see all clients / fall back to the default client on ingestion).</summary>
    public const string ClientId = "rsc:client_id";
}
