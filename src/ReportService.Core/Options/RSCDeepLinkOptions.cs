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
}
