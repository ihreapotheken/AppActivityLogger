using System.Text.RegularExpressions;

namespace ReportService.Storage.Catalog;

/// <summary>An app owned by a client. <see cref="Slug"/> is the immutable key the SDK sends as
/// <c>appId</c> — unique <em>within its owning client</em> (<see cref="ClientSlug"/>), so two clients
/// may each have an app with the same slug. Environment is folded into the slug (a client creates a
/// separate app entry per environment, e.g. <c>app-a-qa</c> / <c>app-a-prod</c>) — there is no
/// separate environment axis. <see cref="DisplayName"/> is editable.</summary>
public sealed record RSCAppRecord(
    string Id,
    string ClientSlug,
    string Slug,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt)
{
    public bool IsArchived => ArchivedAt is not null;
}

/// <summary>An admin-registered client — the top-level tenant, identified by its API access key. A
/// client owns a list of <see cref="RSCAppRecord"/> apps, each surfaced as its own dashboard.</summary>
public sealed record RSCClientRecord(
    string Id,
    string Slug,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt)
{
    public bool IsArchived => ArchivedAt is not null;
}

/// <summary>
/// Tenancy catalog: the registry of clients (the top-level tenant, keyed by API access key) and the
/// apps each client owns (each with its own environment list). Apps are nested under clients, so app
/// operations and the <c>IsValid*</c> hot-path checks are all scoped by a client slug. The
/// <c>IsValid*</c> methods are the synchronous, in-memory-cache-backed path the ingestion validator
/// calls on every batch (no DB round-trip); the cache is refreshed after every mutation. Mutations
/// hit SQLite.
/// </summary>
public interface RSCICatalog
{
    // -------- Hot-path validation (cache-backed, synchronous) --------

    /// <summary>True iff an active client with this slug exists. Slug is normalized by the caller.</summary>
    bool IsValidClient(string clientSlug);

    /// <summary>True iff <paramref name="clientSlug"/> is an active client that owns an active app
    /// with <paramref name="appSlug"/>. (Environment is folded into the slug — no separate check.)</summary>
    bool IsValidApp(string clientSlug, string appSlug);

    // -------- Apps (client-scoped) --------

    /// <summary>The apps owned by one client.</summary>
    Task<IReadOnlyList<RSCAppRecord>> ListAppsAsync(string clientSlug, bool includeArchived, CancellationToken ct);

    /// <summary>Every app across all clients (admin oversight + per-app dashboard navigation). Each
    /// record carries its owning <see cref="RSCAppRecord.ClientSlug"/>.</summary>
    Task<IReadOnlyList<RSCAppRecord>> ListAllAppsAsync(bool includeArchived, CancellationToken ct);

    Task<RSCAppRecord?> GetAppAsync(string clientSlug, string appSlug, CancellationToken ct);

    /// <summary>Register a new app under a client. The slug carries any environment distinction
    /// (e.g. <c>app-a-qa</c>). Throws <see cref="RSCCatalogException"/> on an invalid/unknown client,
    /// an invalid slug, or a slug already used by that client.</summary>
    Task<RSCAppRecord> CreateAppAsync(string clientSlug, string appSlug, string displayName, CancellationToken ct);

    Task<bool> RenameAppAsync(string clientSlug, string appSlug, string displayName, CancellationToken ct);

    /// <summary>Soft-disable an app: ingestion is rejected and it drops out of the dashboards, but the
    /// row and its on-disk data are kept. Reversible via <see cref="UnarchiveAppAsync"/>.</summary>
    Task<bool> ArchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct);

    /// <summary>Restore an archived app (clear <c>archived_at</c>). No-op (returns false) if the app
    /// isn't archived or doesn't exist.</summary>
    Task<bool> UnarchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct);

    /// <summary>Permanently remove an app's catalog row (hard delete, unlike <see cref="ArchiveAppAsync"/>).
    /// The caller is responsible for purging the app's on-disk data. Refuses the seeded default app of the
    /// default client (throws <see cref="RSCCatalogException"/>). Returns false if the app didn't exist.</summary>
    Task<bool> DeleteAppAsync(string clientSlug, string appSlug, CancellationToken ct);

    // -------- Clients --------
    Task<IReadOnlyList<RSCClientRecord>> ListClientsAsync(bool includeArchived, CancellationToken ct);
    Task<RSCClientRecord?> GetClientAsync(string slug, CancellationToken ct);
    Task<RSCClientRecord> CreateClientAsync(string slug, string displayName, CancellationToken ct);
    Task<bool> RenameClientAsync(string slug, string displayName, CancellationToken ct);

    /// <summary>Soft-disable a whole tenant: ingestion under any of its keys is rejected and all its apps
    /// drop out of the dashboards (an app is only active when its owning client is active too), but every
    /// row and all on-disk data are kept. Reversible via <see cref="UnarchiveClientAsync"/>. Refuses the
    /// default client (throws <see cref="RSCCatalogException"/>).</summary>
    Task<bool> ArchiveClientAsync(string slug, CancellationToken ct);

    /// <summary>Restore an archived client (clear <c>archived_at</c>); its apps become visible again in
    /// exactly the per-app archived states they had before. No-op (returns false) if not archived.</summary>
    Task<bool> UnarchiveClientAsync(string slug, CancellationToken ct);

    /// <summary>Permanently remove a client and all its apps from the catalog (hard delete, unlike
    /// <see cref="ArchiveClientAsync"/>). The caller is responsible for purging the client's on-disk data
    /// and revoking its API keys. Refuses the default client (throws <see cref="RSCCatalogException"/>).
    /// Returns false if the client didn't exist.</summary>
    Task<bool> DeleteClientAsync(string slug, CancellationToken ct);

    // -------- Status dashboard --------
    Task<int> CountActiveAppsAsync(CancellationToken ct);
    Task<int> CountActiveClientsAsync(CancellationToken ct);
}

/// <summary>Thrown by catalog mutations on caller-fixable input errors (bad slug, duplicate,
/// empty environment list). The admin page catches it and renders the message as a flash.</summary>
public sealed class RSCCatalogException : Exception
{
    public RSCCatalogException(string message) : base(message) { }
}

/// <summary>Shared slug + environment normalization/validation used by the store and admin pages.</summary>
public static partial class RSCCatalogSlug
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SlugRegex();

    /// <summary>Trim + lowercase. Applied to slugs, environments, and inbound attribution alike so
    /// matching is case/whitespace stable.</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid(string normalizedSlug) => SlugRegex().IsMatch(normalizedSlug);

    /// <summary>Normalize + validate, throwing <see cref="RSCCatalogException"/> with a friendly
    /// message on failure. Returns the normalized slug.</summary>
    public static string Require(string? value, string what)
    {
        var slug = Normalize(value);
        if (!IsValid(slug))
            throw new RSCCatalogException(
                $"{what} must be lowercase letters, digits, and hyphens (1-64 chars, not starting with a hyphen).");
        return slug;
    }
}
