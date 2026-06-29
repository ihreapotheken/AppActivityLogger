using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.DeepLinks;
using ReportService.Options;

namespace ReportService.Admin.Pages;

/// <summary>
/// Operator console for deferred deep linking. Manages the link definitions (a page pattern → a
/// redirect address) and surfaces the recorded website clicks the match endpoint correlates against.
/// Mutations redirect back with a <c>?flash=</c> toast, mirroring the Forced reports page.
/// </summary>
public sealed partial class RSADeepLinksModel : PageModel
{
    private readonly RSCIDeferredDeepLinkStore _store;
    private readonly RSCDeepLinkOptions _options;

    public RSADeepLinksModel(RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions options)
    {
        _store = store;
        _options = options;
    }

    [BindProperty(SupportsGet = true, Name = "edit")] public string? EditSlug { get; set; }

    public IReadOnlyList<RSCDeferredDeepLink> Links { get; private set; } = Array.Empty<RSCDeferredDeepLink>();
    public IReadOnlyList<RSCDeferredDeepLinkClick> Clicks { get; private set; } = Array.Empty<RSCDeferredDeepLinkClick>();
    public RSCDeferredDeepLink? Editing { get; private set; }
    public int MatchWindowHours => _options.MatchWindowHours;
    public string? Flash { get; private set; }
    public string? FlashKind { get; private set; }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,127}$")]
    private static partial Regex SlugPattern();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Links = await _store.ListLinksAsync(ct).ConfigureAwait(false);
        Clicks = await _store.ListRecentClicksAsync(_options.RecentClicksLimit, ct).ConfigureAwait(false);

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
        [FromForm] string? redirectUrl, [FromForm] bool enabled, CancellationToken ct)
    {
        slug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        name = (name ?? string.Empty).Trim();
        pagePattern = (pagePattern ?? string.Empty).Trim();
        redirectUrl = (redirectUrl ?? string.Empty).Trim();

        if (!SlugPattern().IsMatch(slug))
            return Err("Slug must be 1–128 chars: lowercase letters, digits, hyphens (starting with a letter or digit).");
        if (name.Length is 0 or > 256)
            return Err("Name is required (max 256 chars).");
        if (pagePattern.Length is 0 or > 2048)
            return Err("Page pattern is required (max 2048 chars).");
        if (redirectUrl.Length is 0 or > 2048 || !Uri.TryCreate(redirectUrl, UriKind.Absolute, out _))
            return Err("Redirect address must be an absolute URL (e.g. https://… or myapp://…).");

        var inserted = await _store.UpsertLinkAsync(slug, name, pagePattern, redirectUrl, enabled, ct)
            .ConfigureAwait(false);
        return RedirectToPage(new { flash = $"{(inserted ? "created" : "updated")}: {slug}", kind = "ok" });
    }

    public async Task<IActionResult> OnPostToggleAsync([FromForm] string slug, [FromForm] bool enabled, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return RedirectToPage();
        var ok = await _store.SetLinkEnabledAsync(slug.Trim(), enabled, ct).ConfigureAwait(false);
        return RedirectToPage(new
        {
            flash = ok ? $"{(enabled ? "enabled" : "disabled")}: {slug}" : $"not found: {slug}",
            kind = ok ? "ok" : "err"
        });
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromForm] string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return RedirectToPage();
        var removed = await _store.DeleteLinkAsync(slug.Trim(), ct).ConfigureAwait(false);
        return RedirectToPage(new
        {
            flash = removed ? $"deleted: {slug}" : $"not found: {slug}",
            kind = removed ? "ok" : "err"
        });
    }

    private IActionResult Err(string message) => RedirectToPage(new { flash = message, kind = "err" });
}
