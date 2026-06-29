namespace ReportService.DeepLinks;

/// <summary>
/// Storage surface for the deferred deep-linking subsystem. Owns both the operator-managed link
/// definitions and the recorded website clicks. The single implementation today is
/// <see cref="RSCSqliteDeferredDeepLinkStore"/>.
/// </summary>
public interface RSCIDeferredDeepLinkStore
{
    // -------- Link definitions (admin-managed) --------

    /// <summary>All configured links, most recently updated first.</summary>
    Task<IReadOnlyList<RSCDeferredDeepLink>> ListLinksAsync(CancellationToken ct);

    /// <summary>One link by its operator-chosen slug, or null if none.</summary>
    Task<RSCDeferredDeepLink?> GetLinkBySlugAsync(string slug, CancellationToken ct);

    /// <summary>
    /// Inserts a new link or updates the existing one with the same <paramref name="slug"/>.
    /// Returns true when a new row was inserted, false when an existing one was updated.
    /// </summary>
    Task<bool> UpsertLinkAsync(
        string slug, string name, string pagePattern, string redirectUrl, bool enabled, CancellationToken ct);

    /// <summary>Flips a link's enabled flag. Returns false if the slug is unknown.</summary>
    Task<bool> SetLinkEnabledAsync(string slug, bool enabled, CancellationToken ct);

    /// <summary>Deletes a link by slug. Returns false if the slug is unknown. Recorded clicks are
    /// left untouched (they keep their denormalised slug/redirect snapshot).</summary>
    Task<bool> DeleteLinkAsync(string slug, CancellationToken ct);

    // -------- Click capture + matching --------

    /// <summary>
    /// Records a website visit. The visited <paramref name="pageUrl"/> is matched against the
    /// enabled link definitions (longest matching <c>page_pattern</c> substring wins) and the
    /// resolved link is denormalised onto the stored row. Returns the stored click, including the
    /// resolved match (if any).
    /// </summary>
    Task<RSCDeferredDeepLinkClick> RecordClickAsync(
        string ip, string pageUrl, string? userAgent, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Finds the most recent recorded click for <paramref name="ip"/> within
    /// <paramref name="window"/> that resolved to a link, has not already been claimed, and whose
    /// link is still enabled. When <paramref name="claim"/> is true the matched click is stamped
    /// <c>matched_at = now</c> so it is handed out at most once. Returns null when there is no match.
    /// </summary>
    Task<RSCDeferredDeepLinkMatch?> FindMatchForIpAsync(
        string ip, TimeSpan window, bool claim, DateTimeOffset now, CancellationToken ct);

    /// <summary>Most recent recorded clicks for the admin page, newest first.</summary>
    Task<IReadOnlyList<RSCDeferredDeepLinkClick>> ListRecentClicksAsync(int limit, CancellationToken ct);
}
