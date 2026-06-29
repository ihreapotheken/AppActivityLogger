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
/// resolved to (if any) at capture time. <see cref="MatchedAt"/> is stamped when the match endpoint
/// later hands this click to an app, so a click is consumed at most once.
/// </summary>
public sealed record RSCDeferredDeepLinkClick(
    long Id,
    string Ip,
    string PageUrl,
    string? UserAgent,
    string? LinkSlug,
    string? RedirectUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MatchedAt);

/// <summary>
/// The result the match endpoint returns to an app: the deep link to open plus the page reference
/// and time of the originating click. Null is returned when no recent click for the IP resolves to
/// an enabled link.
/// </summary>
public sealed record RSCDeferredDeepLinkMatch(
    string Slug,
    string Name,
    string RedirectUrl,
    string PageUrl,
    DateTimeOffset ClickedAt);
