namespace ReportService.Options;

/// <summary>
/// Configuration for the deferred deep-linking subsystem. Bound from the <c>DeepLinks</c> section.
/// The feature stores its own SQLite DB (so it works regardless of the report <c>Storage</c> mode
/// and under a read-only content root) and exposes two SDK/website endpoints plus an admin page.
/// </summary>
public sealed record RSCDeepLinkOptions
{
    public const string SectionName = "DeepLinks";

    /// <summary>SQLite file holding deep-link definitions + recorded clicks. Anchored under
    /// <c>ReportsRoot</c> when relative — same convention as the other state DBs.</summary>
    public string SqliteDbPath { get; init; } = "deeplinks.db";

    /// <summary>
    /// How far back the match endpoint looks when correlating an app's IP to a recorded website
    /// click. Deferred deep linking is a best-effort heuristic — a visitor browses, then installs
    /// and opens the app, hopefully from the same network within a short window. Default 24 hours.
    /// </summary>
    public int MatchWindowHours { get; init; } = 24;

    /// <summary>Cap on the number of recent clicks the admin page lists. Keeps the page bounded on
    /// a busy capture stream.</summary>
    public int RecentClicksLimit { get; init; } = 200;

    /// <summary>
    /// Maximum number of query parameters captured per click (from the smart link's query string or
    /// the JSON capture body's <c>params</c>). Excess parameters beyond this cap are dropped — never
    /// rejected — so a smart-link redirect is never broken by an over-decorated URL. Captured params
    /// are stored with the click, forwarded onto the redirect address, and returned on match. The cap
    /// bounds storage and the length of the forwarded redirect. Default 16.
    /// </summary>
    public int MaxQueryParams { get; init; } = 16;

    /// <summary>
    /// Maximum length (characters) of any single captured query-parameter key or value. Longer
    /// keys/values are truncated to this length rather than rejected. Default 256.
    /// </summary>
    public int MaxQueryParamLength { get; init; } = 256;

    /// <summary>
    /// Default age, in days, after which recorded clicks are purged by the background retention
    /// worker. This is the seed value: operators can override it at runtime via the admin-key
    /// <c>/api/v2/deeplinks/click-retention</c> endpoint or the admin page, and the override is
    /// persisted in the deep-link DB. Link definitions are never purged — only the click stream.
    /// Default 30.
    /// </summary>
    public int ClickRetentionDays { get; init; } = 30;

    /// <summary>How often the click-retention sweep runs, in seconds. Floored at 60s. Default 1 hour.</summary>
    public int RetentionScanIntervalSeconds { get; init; } = 3600;

    /// <summary>Page size for the admin links list. Bounds the rows rendered per page so the page
    /// stays responsive with thousands of definitions. Default 50.</summary>
    public int LinksPageSize { get; init; } = 50;
}
