namespace ReportService.DeepLinks;

/// <summary>
/// An operator-configured deferred deep link. When a recorded website click's
/// <see cref="RSCDeferredDeepLinkClick.PageUrl"/> contains <see cref="PagePattern"/> (case-insensitive
/// substring), the click resolves to this link and the app — on first launch from the same IP — is
/// handed <see cref="RedirectUrl"/> to navigate to. The most specific (longest) matching pattern wins.
/// </summary>
public sealed record RSCDeferredDeepLink(
    long Id,
    string Slug,
    string Name,
    string PagePattern,
    string RedirectUrl,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One recorded website visit: the visitor's IP and the page they were on, plus the deep link it
/// resolved to (if any) at capture time, any captured query parameters, and any device
/// identification <see cref="Signals"/> (screen size, browser, timezone, …). <see cref="MatchedAt"/>
/// is stamped when the match endpoint later hands this click to an app, so a click is consumed at
/// most once.
/// </summary>
public sealed record RSCDeferredDeepLinkClick(
    long Id,
    string Ip,
    string PageUrl,
    string? UserAgent,
    string? LinkSlug,
    string? RedirectUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MatchedAt,
    IReadOnlyDictionary<string, string>? QueryParams = null,
    IReadOnlyDictionary<string, string>? Signals = null);

/// <summary>
/// Filter for the admin recent-clicks list, scoped to the request "header data" captured per click.
/// Every field is optional; a null/blank field is not applied (an all-empty filter lists the most
/// recent clicks unfiltered). String filters are case-insensitive substring matches.
/// </summary>
/// <param name="Ip">Substring of the visitor IP (itself derived from the connection / forwarded headers).</param>
/// <param name="UserAgent">Substring of the captured <c>User-Agent</c> header.</param>
/// <param name="Header">Free-text substring matched across the captured header data — the
/// <c>User-Agent</c> and the device-identification <c>Signals</c> (the <c>X-DeepLink-*</c> / client-hint
/// headers, stored as a JSON object). Lets an operator filter by a header key or value (e.g. a
/// browser, platform, language, or a custom <c>X-DeepLink-*</c> value).</param>
/// <param name="Matched"><c>true</c> = only clicks that resolved to a link; <c>false</c> = only
/// unmatched; <c>null</c> = both.</param>
public sealed record RSCDeepLinkClickFilter(
    string? Ip = null,
    string? UserAgent = null,
    string? Header = null,
    bool? Matched = null);

/// <summary>
/// The result the match endpoint returns to an app: the deep link to open plus the page reference,
/// the captured query parameters, the device <see cref="Signals"/>, and the time of the originating
/// click. Null is returned when no recent click for the IP resolves to an enabled link. The endpoint
/// forwards <see cref="QueryParams"/> onto <see cref="RedirectUrl"/> before returning them; signals
/// are returned as-is for the caller to use as extra match confidence.
/// </summary>
public sealed record RSCDeferredDeepLinkMatch(
    string Slug,
    string Name,
    string RedirectUrl,
    string PageUrl,
    DateTimeOffset ClickedAt,
    IReadOnlyDictionary<string, string>? QueryParams = null,
    IReadOnlyDictionary<string, string>? Signals = null);
