using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.DeepLinks;
using ReportService.Options;

namespace ReportService.Admin.Pages;

/// <summary>
/// Operator console for deferred deep linking. Manages the link definitions (a page pattern → a
/// redirect address), the click-retention period, and surfaces the recorded website clicks the match
/// endpoint correlates against. The links list is paginated + searchable so it stays usable with
/// thousands of definitions. Mutations redirect back with a <c>?flash=</c> toast (preserving the
/// current search/page), mirroring the Forced reports page.
/// </summary>
public sealed partial class RSADeepLinksModel : PageModel
{
    private const int MaxRetentionDays = 3650;

    private readonly RSCIDeferredDeepLinkStore _store;
    private readonly RSCDeepLinkOptions _options;

    public RSADeepLinksModel(RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions options)
    {
        _store = store;
        _options = options;
    }

    [BindProperty(SupportsGet = true, Name = "edit")] public string? EditSlug { get; set; }
    [BindProperty(SupportsGet = true, Name = "q")]    public string? Search { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    // Recent-clicks filters, scoped to the captured request "header data".
    [BindProperty(SupportsGet = true, Name = "clickIp")]      public string? ClickIp { get; set; }
    [BindProperty(SupportsGet = true, Name = "clickUa")]      public string? ClickUserAgent { get; set; }
    [BindProperty(SupportsGet = true, Name = "clickHeader")]  public string? ClickHeader { get; set; }
    [BindProperty(SupportsGet = true, Name = "clickMatched")] public string? ClickMatched { get; set; }

    public IReadOnlyList<RSCDeferredDeepLink> Links { get; private set; } = Array.Empty<RSCDeferredDeepLink>();
    public IReadOnlyList<RSCDeferredDeepLinkClick> Clicks { get; private set; } = Array.Empty<RSCDeferredDeepLinkClick>();
    public RSCDeferredDeepLink? Editing { get; private set; }

    public int TotalLinks { get; private set; }
    public int PageSize => Math.Max(1, _options.LinksPageSize);
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalLinks / (double)PageSize));

    public int MatchWindowHours => _options.MatchWindowHours;
    public int MaxQueryParams => _options.MaxQueryParams;
    public int MaxQueryParamLength => _options.MaxQueryParamLength;
    public int ClickRetentionDays { get; private set; }
    public bool ClickRetentionOverridden { get; private set; }

    /// <summary>Absolute base (scheme + host) of the current request, used to render the hosted
    /// smart-link URLs operators hand out. Derived from the request so it reflects whatever
    /// host/scheme the console is reached on.</summary>
    public string LinkBase { get; private set; } = string.Empty;
    public string? Flash { get; private set; }
    public string? FlashKind { get; private set; }

    /// <summary>True when any recent-clicks filter is active — the view shows a "clear filters" link.</summary>
    public bool HasClickFilter =>
        !string.IsNullOrWhiteSpace(ClickIp) || !string.IsNullOrWhiteSpace(ClickUserAgent)
        || !string.IsNullOrWhiteSpace(ClickHeader) || !string.IsNullOrWhiteSpace(ClickMatched);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,127}$")]
    private static partial Regex SlugPattern();

    public async Task OnGetAsync(CancellationToken ct)
    {
        LinkBase = $"{Request.Scheme}://{Request.Host}";

        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        TotalLinks = await _store.CountLinksAsync(search, ct).ConfigureAwait(false);
        if (PageNumber < 1) PageNumber = 1;
        if (PageNumber > TotalPages) PageNumber = TotalPages;
        var offset = (PageNumber - 1) * PageSize;
        Links = await _store.ListLinksAsync(search, PageSize, offset, ct).ConfigureAwait(false);

        var clickFilter = new RSCDeepLinkClickFilter(
            Ip: string.IsNullOrWhiteSpace(ClickIp) ? null : ClickIp.Trim(),
            UserAgent: string.IsNullOrWhiteSpace(ClickUserAgent) ? null : ClickUserAgent.Trim(),
            Header: string.IsNullOrWhiteSpace(ClickHeader) ? null : ClickHeader.Trim(),
            Matched: ClickMatched switch { "matched" => true, "unmatched" => false, _ => (bool?)null });
        Clicks = await _store.ListClicksAsync(clickFilter, _options.RecentClicksLimit, ct).ConfigureAwait(false);

        var persisted = await _store.GetClickRetentionDaysAsync(ct).ConfigureAwait(false);
        ClickRetentionOverridden = persisted is not null;
        ClickRetentionDays = persisted ?? _options.ClickRetentionDays;

        if (!string.IsNullOrWhiteSpace(EditSlug))
            Editing = await _store.GetLinkBySlugAsync(EditSlug.Trim(), ct).ConfigureAwait(false);

        if (Request.Query.TryGetValue("flash", out var flash))
        {
            Flash = flash.ToString();
            FlashKind = Request.Query.TryGetValue("kind", out var kind) ? kind.ToString() : "ok";
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(
        [FromForm] string? slug, [FromForm] string? name, [FromForm] string? pagePattern,
        [FromForm] string? redirectUrl, [FromForm] string? redirectUrlAndroid, [FromForm] string? redirectUrlIos,
        [FromForm] bool enabled, CancellationToken ct)
    {
        slug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        name = (name ?? string.Empty).Trim();
        pagePattern = (pagePattern ?? string.Empty).Trim();
        redirectUrl = (redirectUrl ?? string.Empty).Trim();
        redirectUrlAndroid = (redirectUrlAndroid ?? string.Empty).Trim();
        redirectUrlIos = (redirectUrlIos ?? string.Empty).Trim();

        if (!SlugPattern().IsMatch(slug))
            return Err("Slug must be 1–128 chars: lowercase letters, digits, hyphens (starting with a letter or digit).");
        if (name.Length is 0 or > 256)
            return Err("Name is required (max 256 chars).");
        if (pagePattern.Length is 0 or > 2048)
            return Err("Page pattern is required (max 2048 chars).");
        if (redirectUrl.Length is 0 or > 2048 || !Uri.TryCreate(redirectUrl, UriKind.Absolute, out _))
            return Err("Redirect address must be an absolute URL (e.g. https://… or myapp://…).");
        if (!IsValidOverride(redirectUrlAndroid))
            return Err("Android redirect address must be an absolute URL (max 2048 chars), or left blank.");
        if (!IsValidOverride(redirectUrlIos))
            return Err("iOS redirect address must be an absolute URL (max 2048 chars), or left blank.");

        var inserted = await _store.UpsertLinkAsync(
                slug, name, pagePattern, redirectUrl,
                redirectUrlAndroid.Length == 0 ? null : redirectUrlAndroid,
                redirectUrlIos.Length == 0 ? null : redirectUrlIos,
                enabled, ct)
            .ConfigureAwait(false);
        return Redirect($"{(inserted ? "created" : "updated")}: {slug}", "ok");

        // A platform override is optional; when present it must be a valid absolute URL, like the default.
        static bool IsValidOverride(string value) =>
            value.Length == 0 || (value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out _));
    }

    public async Task<IActionResult> OnPostToggleAsync([FromForm] string slug, [FromForm] bool enabled, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return RedirectPreserving();
        var ok = await _store.SetLinkEnabledAsync(slug.Trim(), enabled, ct).ConfigureAwait(false);
        return Redirect(ok ? $"{(enabled ? "enabled" : "disabled")}: {slug}" : $"not found: {slug}", ok ? "ok" : "err");
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromForm] string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return RedirectPreserving();
        var removed = await _store.DeleteLinkAsync(slug.Trim(), ct).ConfigureAwait(false);
        return Redirect(removed ? $"deleted: {slug}" : $"not found: {slug}", removed ? "ok" : "err");
    }

    public async Task<IActionResult> OnPostRetentionAsync([FromForm] int retentionDays, CancellationToken ct)
    {
        if (retentionDays is < 1 or > MaxRetentionDays)
            return Err($"Retention must be between 1 and {MaxRetentionDays} days.");
        await _store.SetClickRetentionDaysAsync(retentionDays, ct).ConfigureAwait(false);
        return Redirect($"click retention set to {retentionDays} days", "ok");
    }

    // Redirects preserve the current search + page so an edit/toggle/delete doesn't bounce the
    // operator back to an unfiltered page 1 of a thousands-long list.
    //
    // Built as a plain local URL rather than RedirectToPage(routeValues): `page` is a RESERVED
    // Razor Pages route key (the page-path slot), so passing it as a route value collides with the
    // ambient page and throws "No page named '' matches the supplied values" at execution time. A
    // query-string redirect sidesteps the reserved-key handling entirely; the pager still binds it
    // back via [BindProperty(Name = "page")].
    private IActionResult Redirect(string flash, string kind) => LocalRedirect(BuildSelfUrl(flash, kind));

    private IActionResult RedirectPreserving() => LocalRedirect(BuildSelfUrl(null, null));

    private string BuildSelfUrl(string? flash, string? kind)
    {
        var qp = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Search)) qp["q"] = Search;
        if (PageNumber > 1) qp["page"] = PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(flash)) qp["flash"] = flash;
        if (!string.IsNullOrWhiteSpace(kind)) qp["kind"] = kind;
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/DeepLinks", qp);
    }

    private IActionResult Err(string message) => Redirect(message, "err");
}
