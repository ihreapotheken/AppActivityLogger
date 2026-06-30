using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Storage.ApiKeys;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

/// <summary>
/// Operator console for <b>clients</b> — the top-level tenant, identified by an API access key.
/// Registering a client mints its first access key (returned once); the client uses that key to
/// ingest analytics and to self-manage its apps via <c>/api/v2/apps</c>. You can issue additional
/// keys for a client, rename it, or archive it. Each client owns its own apps (managed on
/// <c>/Apps</c>), each surfaced as its own dashboard.
/// </summary>
public sealed class RSAClientsModel : PageModel
{
    private readonly RSCICatalog _catalog;
    private readonly RSCIApiKeyStore _keys;

    public RSAClientsModel(RSCICatalog catalog, RSCIApiKeyStore keys)
    {
        _catalog = catalog;
        _keys = keys;
    }

    public IReadOnlyList<RSCClientRecord> Clients { get; private set; } = Array.Empty<RSCClientRecord>();

    /// <summary>Active access-key count per client slug, for the table.</summary>
    public IReadOnlyDictionary<string, int> KeyCounts { get; private set; } = new Dictionary<string, int>();

    public string? Flash { get; private set; }
    public string? FlashKind { get; private set; }

    /// <summary>One-time plaintext of a just-minted key + the client it's for. Carried via TempData
    /// (never the redirect URL) so the secret isn't logged in access logs / browser history.</summary>
    public string? NewKey { get; private set; }
    public string? NewKeyClient { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Clients = await _catalog.ListClientsAsync(includeArchived: true, ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var allKeys = await _keys.ListAsync(ct).ConfigureAwait(false);
        KeyCounts = allKeys
            .Where(k => k.ClientId is not null && k.IsActive(now))
            .GroupBy(k => k.ClientId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        if (TempData.TryGetValue("NewKey", out var nk)) NewKey = nk as string;
        if (TempData.TryGetValue("NewKeyClient", out var nkc)) NewKeyClient = nkc as string;

        if (Request.Query.TryGetValue("flash", out var flash))
        {
            Flash = flash.ToString();
            FlashKind = Request.Query.TryGetValue("kind", out var kind) ? kind.ToString() : "ok";
        }
    }

    public async Task<IActionResult> OnPostCreateAsync([FromForm] string? slug, [FromForm] string? displayName, CancellationToken ct)
    {
        RSCClientRecord client;
        try
        {
            client = await _catalog.CreateClientAsync(slug ?? string.Empty, displayName ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (RSCCatalogException ex) { return Err(ex.Message); }

        // Mint the client's first access key (the key IS the client's identity). User role: a client
        // key ingests + manages its own apps, but can't manage other keys.
        var minted = await MintClientKeyAsync(client.Slug, ct).ConfigureAwait(false);
        if (minted is null)
            return Ok($"registered client '{client.Slug}' (key store unavailable — issue a key once it recovers)");

        return OkWithKey($"registered client '{client.Slug}' and minted its access key", client.Slug, minted);
    }

    public async Task<IActionResult> OnPostIssueKeyAsync([FromForm] string slug, CancellationToken ct)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!_catalog.IsValidClient(normalized)) return Err($"unknown client: {slug}");
        var minted = await MintClientKeyAsync(normalized, ct).ConfigureAwait(false);
        return minted is null
            ? Err("could not mint a key (key store unavailable).")
            : OkWithKey($"issued a new access key for '{normalized}'", normalized, minted);
    }

    public async Task<IActionResult> OnPostRenameAsync([FromForm] string slug, [FromForm] string? displayName, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.RenameClientAsync(slug, displayName ?? string.Empty, ct).ConfigureAwait(false);
            return ok ? Ok($"renamed '{slug}'") : Err($"client not found: {slug}");
        }
        catch (RSCCatalogException ex) { return Err(ex.Message); }
    }

    public async Task<IActionResult> OnPostArchiveAsync([FromForm] string slug, CancellationToken ct)
    {
        var ok = await _catalog.ArchiveClientAsync(slug, ct).ConfigureAwait(false);
        return ok ? Ok($"archived '{slug}'") : Err($"client not found / already archived: {slug}");
    }

    private async Task<string?> MintClientKeyAsync(string clientSlug, CancellationToken ct)
    {
        try
        {
            var actor = User?.Identity?.Name ?? "operator";
            var created = await _keys.CreateAsync(
                RSCApiKeyRoles.User, label: $"client:{clientSlug}", expiresAt: null,
                rateLimitPerMinute: null, createdBy: actor, ct, clientId: clientSlug).ConfigureAwait(false);
            return created.PlaintextKey;
        }
        catch (InvalidOperationException) { return null; }
    }

    private IActionResult OkWithKey(string flash, string clientSlug, string plaintextKey)
    {
        TempData["NewKey"] = plaintextKey;
        TempData["NewKeyClient"] = clientSlug;
        return RedirectToPage(new { flash, kind = "ok" });
    }

    private IActionResult Ok(string flash) => RedirectToPage(new { flash, kind = "ok" });
    private IActionResult Err(string flash) => RedirectToPage(new { flash, kind = "err" });
}
