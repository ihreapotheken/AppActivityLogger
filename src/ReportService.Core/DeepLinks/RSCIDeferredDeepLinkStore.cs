namespace ReportService.DeepLinks;

/// <summary>
/// Storage surface for the deferred deep-linking subsystem. Owns both the operator-managed link
/// definitions and the recorded website clicks. The single implementation today is
/// <see cref="RSCSqliteDeferredDeepLinkStore"/>.
/// </summary>
public interface RSCIDeferredDeepLinkStore
{
    // -------- Link definitions (admin-managed) --------

    /// <summary>
    /// A page of configured links, most recently updated first. <paramref name="search"/> (when
    /// non-blank) filters by a case-insensitive substring of slug/name/page-pattern. Paginated so the
    /// admin surface stays bounded with thousands of definitions.
    /// </summary>
    Task<IReadOnlyList<RSCDeferredDeepLink>> ListLinksAsync(string? search, int limit, int offset, CancellationToken ct);

    /// <summary>Total links matching the same <paramref name="search"/> filter, for the pager.</summary>
    Task<int> CountLinksAsync(string? search, CancellationToken ct);

    /// <summary>One link by its operator-chosen slug, or null if none.</summary>
    Task<RSCDeferredDeepLink?> GetLinkBySlugAsync(string slug, CancellationToken ct);

    /// <summary>
    /// Inserts a new link or updates the existing one with the same <paramref name="slug"/>.
    /// <paramref name="redirectUrl"/> is the default/fallback address; <paramref name="redirectUrlAndroid"/>
    /// and <paramref name="redirectUrlIos"/> are optional platform-specific overrides (null = no
    /// override, that platform serves the default). Returns true when a new row was inserted, false
    /// when an existing one was updated.
    /// </summary>
    Task<bool> UpsertLinkAsync(
        string slug, string name, string pagePattern, string redirectUrl,
        string? redirectUrlAndroid, string? redirectUrlIos, bool enabled, CancellationToken ct);

    /// <summary>Flips a link's enabled flag. Returns false if the slug is unknown.</summary>
    Task<bool> SetLinkEnabledAsync(string slug, bool enabled, CancellationToken ct);

    /// <summary>Deletes a link by slug. Returns false if the slug is unknown. Recorded clicks are
    /// left untouched (they keep their denormalised slug/redirect snapshot).</summary>
    Task<bool> DeleteLinkAsync(string slug, CancellationToken ct);

    // -------- Click capture + matching --------

    /// <summary>
    /// Records a website visit. The visited <paramref name="pageUrl"/> is matched against the
    /// enabled link definitions (longest matching <c>page_pattern</c> substring wins) and the
    /// resolved link is denormalised onto the stored row. <paramref name="queryParams"/> and
    /// <paramref name="signals"/> (both already normalised/capped by the caller) are stored with the
    /// click — the former is later forwarded onto the redirect, the latter is device-identification
    /// metadata. The denormalised redirect snapshot is resolved for <paramref name="platform"/>
    /// (android/ios → that platform's override when set, else the link's default; null/unknown → the
    /// default). Returns the stored click, including the resolved match (if any).
    /// </summary>
    Task<RSCDeferredDeepLinkClick> RecordClickAsync(
        string ip, string pageUrl, string? userAgent,
        IReadOnlyDictionary<string, string>? queryParams, IReadOnlyDictionary<string, string>? signals,
        string? platform, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Records a click bound to a <em>known</em> link — used by the hosted smart-link redirect
    /// (<c>GET /dl/{slug}</c>) where the slug already names the link, so no page-pattern resolution
    /// is needed. The click is denormalised to <paramref name="link"/>'s slug + the redirect address
    /// resolved for <paramref name="platform"/> (android/ios override when set, else the default), and
    /// <paramref name="queryParams"/> + <paramref name="signals"/> (already normalised/capped) are
    /// stored with it.
    /// </summary>
    Task<RSCDeferredDeepLinkClick> RecordClickForLinkAsync(
        RSCDeferredDeepLink link, string ip, string pageUrl, string? userAgent,
        IReadOnlyDictionary<string, string>? queryParams, IReadOnlyDictionary<string, string>? signals,
        string? platform, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Finds the most recent recorded click for <paramref name="ip"/> within
    /// <paramref name="window"/> that resolved to a link, has not already been claimed, and whose
    /// link is still enabled. The returned redirect is resolved for <paramref name="platform"/> — the
    /// caller's (app's) platform when supplied, otherwise the <c>platform</c> device signal captured
    /// on the originating click — falling back to the link's default address. When
    /// <paramref name="claim"/> is true the matched click is stamped <c>matched_at = now</c> so it is
    /// handed out at most once. Returns null when there is no match.
    /// </summary>
    Task<RSCDeferredDeepLinkMatch?> FindMatchForIpAsync(
        string ip, TimeSpan window, bool claim, DateTimeOffset now, string? platform, CancellationToken ct);

    /// <summary>Most recent recorded clicks for the admin page, newest first.</summary>
    Task<IReadOnlyList<RSCDeferredDeepLinkClick>> ListRecentClicksAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Most recent recorded clicks matching <paramref name="filter"/> (newest first), for the admin
    /// page's filterable recent-clicks view. The filter narrows by the captured request "header data"
    /// — IP, <c>User-Agent</c>, a free-text search across the User-Agent + device-identification
    /// signals, and matched/unmatched state. An all-empty filter is equivalent to
    /// <see cref="ListRecentClicksAsync"/>.
    /// </summary>
    Task<IReadOnlyList<RSCDeferredDeepLinkClick>> ListClicksAsync(RSCDeepLinkClickFilter filter, int limit, CancellationToken ct);

    // -------- Click retention (runtime-configurable) --------

    /// <summary>The persisted click-retention override in days, or null when unset (caller falls
    /// back to the configured default).</summary>
    Task<int?> GetClickRetentionDaysAsync(CancellationToken ct);

    /// <summary>Persists the click-retention override (in days). Takes effect on the next sweep.</summary>
    Task SetClickRetentionDaysAsync(int days, CancellationToken ct);

    /// <summary>Deletes recorded clicks older than <paramref name="cutoff"/>. Returns the row count
    /// deleted. Link definitions are never touched. Used by the background retention worker.</summary>
    Task<int> PurgeClicksOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
}
