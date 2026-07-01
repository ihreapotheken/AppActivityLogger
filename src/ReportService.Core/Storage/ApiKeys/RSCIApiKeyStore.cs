namespace ReportService.Storage.ApiKeys;

/// <summary>
/// Role of an API key — there are exactly two, and role determines binding:
/// <list type="bullet">
///   <item><c>admin</c>: read + write across <b>all</b> clients — manage keys, ingest for any client,
///         read every dashboard. Always <b>unbound</b> (no <c>client_id</c>). The static root key is
///         admin.</item>
///   <item><c>client</c>: scoped to <b>one</b> client and always <b>bound</b> (carries a
///         <c>client_id</c>). It ingests only its own client's telemetry and reads only its own
///         dashboards; it cannot manage keys or touch another tenant.</item>
/// </list>
/// There is no unbound non-admin ("legacy") key.
/// </summary>
public static class RSCApiKeyRoles
{
    public const string Admin = "admin";
    public const string Client = "client";

    public static bool IsValid(string? role) => role is Admin or Client;
}

/// <summary>
/// Minimal record returned by the hot-path <see cref="RSCIApiKeyStore.Resolve"/> — just what the auth
/// handler and rate limiter need to make a decision. Never carries the hash or plaintext.
/// </summary>
public sealed record RSCApiKeyRecord(
    string Id,
    string Role,
    int? RateLimitPerMinute,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? ClientId = null);

/// <summary>Operator/audit-facing metadata for a key. Excludes the hash and plaintext.</summary>
public sealed record RSCApiKeyMetadata(
    string Id,
    string Role,
    string? Label,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    int? RateLimitPerMinute,
    DateTimeOffset? LastUsedAt,
    string? ClientId = null)
{
    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);
}

/// <summary>Result of minting a key: the durable metadata plus the one-time plaintext secret.</summary>
public sealed record RSCApiKeyCreated(RSCApiKeyMetadata Metadata, string PlaintextKey);

/// <summary>
/// Store for managed API keys. <see cref="Resolve"/> is a synchronous, in-memory cache lookup so the
/// auth handler and the rate-limiter partitioner can call it on every request without a DB round-trip;
/// the cache is refreshed after every mutation. Mutations (<see cref="CreateAsync"/>,
/// <see cref="RevokeAsync"/>) hit SQLite.
/// </summary>
public interface RSCIApiKeyStore
{
    /// <summary>
    /// Resolve a presented key to a valid record, or <c>null</c> if it's unknown, revoked, or expired.
    /// Synchronous + cache-backed (no DB hit). Hashes the presented key and looks it up.
    /// </summary>
    RSCApiKeyRecord? Resolve(string presentedKey);

    /// <summary>Mint a new key. Returns metadata + the one-time plaintext (never stored).
    /// <paramref name="clientId"/> binds the key to a catalog client (the key becomes that client's
    /// identity). It is <b>required for a <c>client</c> role key and must be null for an <c>admin</c>
    /// key</b> (admin spans all clients) — the store enforces this.</summary>
    Task<RSCApiKeyCreated> CreateAsync(
        string role,
        string? label,
        DateTimeOffset? expiresAt,
        int? rateLimitPerMinute,
        string createdBy,
        CancellationToken ct,
        string? clientId = null);

    /// <summary>All keys' metadata, newest first. Never returns hashes or plaintext.</summary>
    Task<IReadOnlyList<RSCApiKeyMetadata>> ListAsync(CancellationToken ct);

    /// <summary>Revoke a key by id. Returns false if no active key with that id exists.</summary>
    Task<bool> RevokeAsync(string id, string revokedBy, CancellationToken ct);

    /// <summary>Count of currently-active (not revoked, not expired) keys. For the Status dashboard.</summary>
    Task<int> CountActiveAsync(CancellationToken ct);
}
