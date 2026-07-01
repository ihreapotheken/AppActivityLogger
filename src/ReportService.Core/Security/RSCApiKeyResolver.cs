using ReportService.Options;
using ReportService.Storage.ApiKeys;

namespace ReportService.Security;

/// <summary>Outcome of resolving a presented API key: who it is, its role, its effective per-minute
/// rate limit, and the catalog client it's bound to (<c>null</c> only for <c>admin</c> keys — the
/// static root key and any managed admin key — which span all clients; a <c>client</c> key always
/// carries its binding). Shared by the auth handler and the rate limiter so they never disagree.</summary>
public sealed record RSCApiKeyResolution(string KeyId, string Role, int EffectiveRateLimitPerMinute, string? ClientId = null);

/// <summary>
/// Single source of truth for turning a presented <c>apiKey</c> header into an identity + limit.
/// Precedence: (1) the configured static key is the permanent root-admin (<see cref="ConfigRootKeyId"/>),
/// then (2) a managed DB key via <see cref="RSCIApiKeyStore.Resolve"/>, else (3) <c>null</c> (unknown,
/// expired, or revoked). The effective limit is the per-key override if set, otherwise the role tier,
/// otherwise the global default.
/// </summary>
public static class RSCApiKeyResolver
{
    public const string ConfigRootKeyId = "config-root";

    public static RSCApiKeyResolution? Resolve(string? presentedKey, RSCReportServiceOptions options, RSCIApiKeyStore store)
    {
        if (string.IsNullOrEmpty(presentedKey)) return null;

        // (1) Static config key — permanent root-admin, never in the DB. Constant-time compare.
        if (RSCSecretComparer.Matches(presentedKey, options.ApiKey))
            return new RSCApiKeyResolution(ConfigRootKeyId, RSCApiKeyRoles.Admin, AdminLimit(options, null));

        // (2) Managed DB key (cache-backed, already validity-checked for revoke/expiry).
        var rec = store.Resolve(presentedKey);
        if (rec is null) return null;

        var limit = rec.Role == RSCApiKeyRoles.Admin
            ? AdminLimit(options, rec.RateLimitPerMinute)
            : ClientLimit(options, rec.RateLimitPerMinute);
        return new RSCApiKeyResolution(rec.Id, rec.Role, limit, rec.ClientId);
    }

    private static int AdminLimit(RSCReportServiceOptions o, int? perKeyOverride) =>
        perKeyOverride ?? (o.ApiKeyAdminRateLimitPerMinute > 0 ? o.ApiKeyAdminRateLimitPerMinute : o.RateLimitPermitsPerMinute);

    // The non-admin (client) rate tier. The backing option keeps its historical name
    // (ApiKeyUserRateLimitPerMinute) to avoid a config break; it now applies to client keys.
    private static int ClientLimit(RSCReportServiceOptions o, int? perKeyOverride) =>
        perKeyOverride ?? (o.ApiKeyUserRateLimitPerMinute > 0 ? o.ApiKeyUserRateLimitPerMinute : o.RateLimitPermitsPerMinute);
}
