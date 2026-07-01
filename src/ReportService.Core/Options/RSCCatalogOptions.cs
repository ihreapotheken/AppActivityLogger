namespace ReportService.Options;

/// <summary>
/// Configuration for the tenancy catalog — the admin-managed registry of <b>apps</b> (each with its
/// own list of <b>environments</b>) and <b>clients</b> that analytics events are differentiated by.
/// Bound from the <c>Catalog</c> configuration section. Lives in its own SQLite DB so it works under
/// a read-only content root and regardless of the report <c>Storage</c> mode, mirroring
/// <c>api-keys.db</c>.
/// </summary>
public sealed record RSCCatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// Master switch for catalog <b>validation</b>. When <c>true</c> (default), an ingested batch whose
    /// app / environment / client is not registered is rejected to the dead-letter queue. When
    /// <c>false</c>, attribution is still stamped (and defaulted) but never rejected — useful for a
    /// phased rollout while clients are still being onboarded. The default app / environment / client
    /// are seeded either way, so flipping this on later never strands previously-defaulted data.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>SQLite file backing the catalog. Anchored under <c>ReportsRoot</c> when relative.</summary>
    public string SqliteDbPath { get; init; } = "catalog.db";

    /// <summary>App slug stamped on a batch that arrives without one (older SDK builds). Seeded at boot.</summary>
    public string DefaultAppSlug { get; init; } = "default";

    /// <summary>Environment value stamped on the (vestigial) <c>environment</c> column of analytics
    /// rows that arrive without one. Environment is no longer a tenancy axis — it is folded into the
    /// app slug (a client creates a separate app per environment, e.g. <c>app-a-qa</c>) — so this is
    /// just the default fill for an unfiltered legacy column, not a validated value.</summary>
    public string DefaultEnvironment { get; init; } = "production";

    /// <summary>Client slug stamped on a batch that arrives without one. Seeded at boot.</summary>
    public string DefaultClientSlug { get; init; } = "default";

    /// <summary>Per-statement SQLite command timeout for the catalog DB.</summary>
    public int SqliteCommandTimeoutSeconds { get; init; } = 10;

    /// <summary>Apps to seed on startup if missing (INSERT-only, won't overwrite operator edits).
    /// Empty in production by default; dev appsettings supplies App A / App B.</summary>
    public RSCCatalogAppSeed[] SeedApps { get; init; } = Array.Empty<RSCCatalogAppSeed>();

    /// <summary>Clients to seed on startup if missing (INSERT-only). Empty in production by default.</summary>
    public RSCCatalogClientSeed[] SeedClients { get; init; } = Array.Empty<RSCCatalogClientSeed>();
}

/// <summary>Plain config DTO for a seed app. <see cref="ClientSlug"/> is the owning client; blank or
/// unregistered falls back to the default client. Environment is folded into the slug (seed a separate
/// app per environment, e.g. <c>app-a-qa</c> / <c>app-a-prod</c>).</summary>
public sealed class RSCCatalogAppSeed
{
    public string ClientSlug { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Plain config DTO for a seed client.</summary>
public sealed class RSCCatalogClientSeed
{
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}
