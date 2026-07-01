namespace ReportService.DeepLinks;

/// <summary>
/// An operator-configured deferred deep link. When a recorded website click's
/// <see cref="RSCDeferredDeepLinkClick.PageUrl"/> contains <see cref="PagePattern"/> (case-insensitive
/// substring), the click resolves to this link and the app — on first launch from the same IP — is
/// handed a redirect address to navigate to. The most specific (longest) matching pattern wins.
/// <para>
/// <see cref="RedirectUrl"/> is the default/fallback address. A link may additionally carry
/// platform-specific overrides — <see cref="RedirectUrlAndroid"/> / <see cref="RedirectUrlIos"/> —
/// so a single slug + page pattern can route an Android visitor to one place (e.g. the Play Store /
/// an Android universal link) and an iOS visitor to another (the App Store / a custom scheme). The
/// platform is resolved at every serving boundary (the hosted <c>/dl/{slug}</c> redirect, the
/// <c>/clicks</c> echo, and the <c>/match</c> response) via <see cref="ResolveRedirect"/>; an unset
/// or unknown platform — and any platform with no override — falls back to <see cref="RedirectUrl"/>.
/// </para>
/// </summary>
public sealed record RSCDeferredDeepLink(
    long Id,
    string Slug,
    string Name,
    string PagePattern,
    string RedirectUrl,
    string? RedirectUrlAndroid,
    string? RedirectUrlIos,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The redirect address for <paramref name="platform"/> — the platform-specific override when one
    /// is configured for that platform, otherwise the default <see cref="RedirectUrl"/>. A null/unknown
    /// platform (anything other than android/ios) also yields the default.
    /// </summary>
    public string ResolveRedirect(string? platform) => RSCDeepLinkPlatform.Normalize(platform) switch
    {
        RSCDeepLinkPlatform.Android => string.IsNullOrEmpty(RedirectUrlAndroid) ? RedirectUrl : RedirectUrlAndroid,
        RSCDeepLinkPlatform.Ios => string.IsNullOrEmpty(RedirectUrlIos) ? RedirectUrl : RedirectUrlIos,
        _ => RedirectUrl,
    };
}

/// <summary>
/// The platform axis a deep link can be specified against: <c>android</c> or <c>ios</c>.
/// <see cref="Normalize"/> folds a raw token — an admin form value, a <c>platform</c> client-hint
/// signal (which arrives quoted, e.g. <c>"Android"</c>), an app-supplied query/body value, or a
/// user-agent-derived guess — to one of those two, or null when it is neither.
/// </summary>
public static class RSCDeepLinkPlatform
{
    public const string Android = "android";
    public const string Ios = "ios";

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().Trim('"').ToLowerInvariant();
        return v switch
        {
            "android" => Android,
            "ios" or "iphone" or "ipad" or "ipod" or "ipados" => Ios,
            _ => null,
        };
    }
}

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
