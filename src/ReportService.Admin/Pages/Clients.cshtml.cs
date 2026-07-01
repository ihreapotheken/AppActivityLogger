using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Audit;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.ApiKeys;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

/// <summary>
/// Single operator console for tenancy administration — <b>clients</b>, their <b>apps</b>, and the
/// <b>managed API keys</b>, merged onto one screen (this replaces the former separate /Apps page and
/// the API-keys block on /Maintenance).
/// <list type="bullet">
///   <item><b>Clients</b> — the top-level tenant, identified by an API access key. Registering a client
///   mints its first key (returned once); <c>issue key</c> mints more. Rename / archive.</item>
///   <item><b>Apps</b> — pick a client to create/rename/archive its apps (the slug carries any env
///   distinction, e.g. <c>app-a-qa</c>). Each app is its own dashboard + database.</item>
///   <item><b>API keys</b> — mint/revoke <c>admin</c> operator keys (unbound; read + write across all
///   clients + manage keys). A client's own <c>client</c> key (bound, scoped to its data) is minted
///   above via <c>issue key</c>, not here.</item>
/// </list>
/// </summary>
public sealed class RSAClientsModel : PageModel
{
    private readonly RSCICatalog _catalog;
    private readonly RSCIApiKeyStore _keys;
    private readonly RSCIAuditLog _audit;
    private readonly RSCIClientDataPurger _purger;
    private readonly ILogger<RSAClientsModel> _logger;

    public RSAClientsModel(
        RSCICatalog catalog, RSCIApiKeyStore keys, RSCIAuditLog audit,
        RSCIClientDataPurger purger, RSCCatalogOptions catalogOptions, ILogger<RSAClientsModel> logger)
    {
        _catalog = catalog;
        _keys = keys;
        _audit = audit;
        _purger = purger;
        _logger = logger;
        DefaultClient = RSCCatalogSlug.Normalize(string.IsNullOrWhiteSpace(catalogOptions.DefaultClientSlug) ? "default" : catalogOptions.DefaultClientSlug);
        DefaultApp = RSCCatalogSlug.Normalize(string.IsNullOrWhiteSpace(catalogOptions.DefaultAppSlug) ? "default" : catalogOptions.DefaultAppSlug);
    }

    /// <summary>The seeded fallback client/app — the view hides archive/delete on them since the
    /// catalog refuses to touch them (they back attribution-omitting traffic).</summary>
    public string DefaultClient { get; }
    public string DefaultApp { get; }

    /// <summary>True iff (client, app) is the protected default app.</summary>
    public bool IsDefaultApp(string clientSlug, string appSlug) =>
        string.Equals(clientSlug, DefaultClient, StringComparison.Ordinal) &&
        string.Equals(appSlug, DefaultApp, StringComparison.Ordinal);

    /// <summary>The client whose apps are managed in the Apps section (null = none picked → "all apps"
    /// overview). Drives the <c>?client=</c> selector shared with the global tenant scope.</summary>
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public IReadOnlyList<RSCClientRecord> Clients { get; private set; } = Array.Empty<RSCClientRecord>();

    /// <summary>Active access-key count per client slug, for the table.</summary>
    public IReadOnlyDictionary<string, int> KeyCounts { get; private set; } = new Dictionary<string, int>();

    /// <summary>The selected client whose apps are shown, or null when listing every client's apps.</summary>
    public string? SelectedClient { get; private set; }
    public IReadOnlyList<RSCAppRecord> Apps { get; private set; } = Array.Empty<RSCAppRecord>();

    /// <summary>Managed API keys (the ingestion API surface), newest store order. The static configured
    /// root key isn't listed.</summary>
    public IReadOnlyList<RSCApiKeyMetadata> ApiKeys { get; private set; } = Array.Empty<RSCApiKeyMetadata>();
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;

    public string? Flash { get; private set; }
    public string? FlashKind { get; private set; }

    /// <summary>One-time plaintext of a just-minted <b>client</b> key + the client it's for. Carried via
    /// TempData (never the redirect URL) so the secret isn't logged in access logs / browser history.</summary>
    public string? NewKey { get; private set; }
    public string? NewKeyClient { get; private set; }

    /// <summary>One-time plaintext of a just-minted <b>managed</b> key + its id (the API-keys section).</summary>
    public string? NewManagedKey { get; private set; }
    public string? NewManagedKeyId { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Clients = await _catalog.ListClientsAsync(includeArchived: true, ct).ConfigureAwait(false);
        SelectedClient = string.IsNullOrWhiteSpace(Client) ? null : Client.Trim().ToLowerInvariant();

        Apps = SelectedClient is null
            ? await _catalog.ListAllAppsAsync(includeArchived: true, ct).ConfigureAwait(false)
            : await _catalog.ListAppsAsync(SelectedClient, includeArchived: true, ct).ConfigureAwait(false);

        Now = DateTimeOffset.UtcNow;
        var allKeys = await _keys.ListAsync(ct).ConfigureAwait(false);
        KeyCounts = allKeys
            .Where(k => k.ClientId is not null && k.IsActive(Now))
            .GroupBy(k => k.ClientId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        ApiKeys = allKeys;

        if (TempData.TryGetValue("NewKey", out var nk)) NewKey = nk as string;
        if (TempData.TryGetValue("NewKeyClient", out var nkc)) NewKeyClient = nkc as string;
        if (TempData.TryGetValue("NewManagedKey", out var mk)) NewManagedKey = mk as string;
        if (TempData.TryGetValue("NewManagedKeyId", out var mki)) NewManagedKeyId = mki as string;

        if (Request.Query.TryGetValue("flash", out var flash))
        {
            Flash = flash.ToString();
            FlashKind = Request.Query.TryGetValue("kind", out var kind) ? kind.ToString() : "ok";
        }
    }

    // ---------------- Clients ----------------

    public async Task<IActionResult> OnPostCreateAsync([FromForm] string? slug, [FromForm] string? displayName, CancellationToken ct)
    {
        RSCClientRecord client;
        try
        {
            client = await _catalog.CreateClientAsync(slug ?? string.Empty, displayName ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "client.create", success: false, target: slug, details: ex.Message).ConfigureAwait(false);
            return Err(ex.Message);
        }
        await _audit.RecordAsync(HttpContext, "client.create", success: true, target: client.Slug).ConfigureAwait(false);

        // Mint the client's first access key (the key IS the client's identity). Client role: the key
        // is bound to this client, ingests + reads only its own data + manages its own apps, and can't
        // manage keys or touch another tenant.
        var minted = await MintClientKeyAsync(client.Slug, ct).ConfigureAwait(false);
        if (minted is null)
            return Ok($"registered client '{client.Slug}' (key store unavailable — issue a key once it recovers)");

        return OkWithClientKey($"registered client '{client.Slug}' and minted its access key", client.Slug, minted);
    }

    public async Task<IActionResult> OnPostIssueKeyAsync([FromForm] string slug, CancellationToken ct)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!_catalog.IsValidClient(normalized)) return Err($"unknown client: {slug}");
        var minted = await MintClientKeyAsync(normalized, ct).ConfigureAwait(false);
        return minted is null
            ? Err("could not mint a key (key store unavailable).")
            : OkWithClientKey($"issued a new access key for '{normalized}'", normalized, minted);
    }

    public async Task<IActionResult> OnPostRenameAsync([FromForm] string slug, [FromForm] string? displayName, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.RenameClientAsync(slug, displayName ?? string.Empty, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "client.rename", success: ok, target: slug).ConfigureAwait(false);
            return ok ? Ok($"renamed '{slug}'") : Err($"client not found: {slug}");
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "client.rename", success: false, target: slug, details: ex.Message).ConfigureAwait(false);
            return Err(ex.Message);
        }
    }

    public async Task<IActionResult> OnPostArchiveAsync([FromForm] string slug, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.ArchiveClientAsync(slug, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "client.archive", success: ok, target: slug).ConfigureAwait(false);
            return ok ? Ok($"archived '{slug}' — ingestion is now rejected and its data is hidden from the dashboards (data kept; restore any time).")
                      : Err($"client not found / already archived: {slug}");
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "client.archive", success: false, target: slug, details: ex.Message).ConfigureAwait(false);
            return Err(ex.Message);
        }
    }

    public async Task<IActionResult> OnPostUnarchiveAsync([FromForm] string slug, CancellationToken ct)
    {
        var ok = await _catalog.UnarchiveClientAsync(slug, ct).ConfigureAwait(false);
        await _audit.RecordAsync(HttpContext, "client.unarchive", success: ok, target: slug).ConfigureAwait(false);
        return ok ? Ok($"restored '{slug}'") : Err($"client not found / not archived: {slug}");
    }

    /// <summary>Permanently delete a client: revoke its API keys, drop its catalog rows (client + all
    /// apps), then purge every byte of its on-disk data. Irreversible — guarded by the confirm dialog
    /// in the view. The default client is refused by the catalog.</summary>
    public async Task<IActionResult> OnPostDeleteAsync([FromForm] string slug, CancellationToken ct)
    {
        var normalized = RSCCatalogSlug.Normalize(slug);
        var client = await _catalog.GetClientAsync(normalized, ct).ConfigureAwait(false);
        if (client is null)
        {
            await _audit.RecordAsync(HttpContext, "client.delete", success: false, target: slug, details: "not found").ConfigureAwait(false);
            return Err($"client not found: {slug}");
        }

        // 1) Revoke every API key bound to this client so its credentials stop authenticating at once.
        var actor = User.Identity?.Name is { Length: > 0 } name ? name : "operator";
        var revoked = 0;
        try
        {
            var keys = await _keys.ListAsync(ct).ConfigureAwait(false);
            foreach (var k in keys.Where(k => string.Equals(k.ClientId, normalized, StringComparison.Ordinal) && !k.IsRevoked))
            {
                if (await _keys.RevokeAsync(k.Id, actor, ct).ConfigureAwait(false)) revoked++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revoking keys for client {Client} during delete failed", normalized);
        }

        // 2) Remove the catalog rows. After this, ingestion validation rejects the client immediately.
        bool removed;
        try
        {
            removed = await _catalog.DeleteClientAsync(normalized, ct).ConfigureAwait(false);
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "client.delete", success: false, target: normalized, details: ex.Message).ConfigureAwait(false);
            return Err(ex.Message);
        }
        if (!removed) return Err($"client not found: {slug}");

        // 3) Purge the on-disk data (every app's analytics + report DBs) and evict the cached handles.
        var purge = _purger.PurgeClientData(normalized);

        await _audit.RecordAsync(HttpContext, "client.delete", success: true, target: normalized,
            details: $"keysRevoked={revoked} dataExisted={purge.DirectoryExisted} dataRemoved={purge.DirectoryRemoved}{(purge.Error is null ? "" : $" error={purge.Error}")}")
            .ConfigureAwait(false);

        var keyNote = revoked == 1 ? "1 key revoked" : $"{revoked} keys revoked";
        if (!purge.Succeeded)
            return Err($"deleted client '{normalized}' ({keyNote}), but on-disk data removal FAILED: {purge.Error}. The data is orphaned and inaccessible — clean it up manually at {purge.Path}.");
        var dataNote = purge.DirectoryExisted ? "data removed" : "no data on disk";
        return Ok($"permanently deleted client '{normalized}' — {keyNote}, {dataNote}.");
    }

    // ---------------- Apps (client-scoped; merged from the former /Apps page) ----------------

    public async Task<IActionResult> OnPostCreateAppAsync(
        [FromForm] string? client, [FromForm] string? slug, [FromForm] string? displayName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client)) return ErrApp("Select a client to create the app under.", client);
        try
        {
            var app = await _catalog.CreateAppAsync(client, slug ?? string.Empty, displayName ?? string.Empty, ct)
                .ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "app.create", success: true, target: $"{app.ClientSlug}/{app.Slug}").ConfigureAwait(false);
            return OkApp($"created app '{app.Slug}' for client '{app.ClientSlug}'", client);
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "app.create", success: false, target: $"{client}/{slug}", details: ex.Message).ConfigureAwait(false);
            return ErrApp(ex.Message, client);
        }
    }

    public async Task<IActionResult> OnPostRenameAppAsync(
        [FromForm] string client, [FromForm] string slug, [FromForm] string? displayName, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.RenameAppAsync(client, slug, displayName ?? string.Empty, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "app.rename", success: ok, target: $"{client}/{slug}").ConfigureAwait(false);
            return ok ? OkApp($"renamed '{slug}'", client) : ErrApp($"app not found: {slug}", client);
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "app.rename", success: false, target: $"{client}/{slug}", details: ex.Message).ConfigureAwait(false);
            return ErrApp(ex.Message, client);
        }
    }

    public async Task<IActionResult> OnPostArchiveAppAsync([FromForm] string client, [FromForm] string slug, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.ArchiveAppAsync(client, slug, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "app.archive", success: ok, target: $"{client}/{slug}").ConfigureAwait(false);
            return ok ? OkApp($"archived '{slug}' (data kept; restore any time)", client) : ErrApp($"app not found / already archived: {slug}", client);
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "app.archive", success: false, target: $"{client}/{slug}", details: ex.Message).ConfigureAwait(false);
            return ErrApp(ex.Message, client);
        }
    }

    public async Task<IActionResult> OnPostUnarchiveAppAsync([FromForm] string client, [FromForm] string slug, CancellationToken ct)
    {
        var ok = await _catalog.UnarchiveAppAsync(client, slug, ct).ConfigureAwait(false);
        await _audit.RecordAsync(HttpContext, "app.unarchive", success: ok, target: $"{client}/{slug}").ConfigureAwait(false);
        return ok ? OkApp($"restored '{slug}'", client) : ErrApp($"app not found / not archived: {slug}", client);
    }

    /// <summary>Permanently delete one app: drop its catalog row, then purge its on-disk data tree.
    /// Irreversible — guarded by the confirm dialog. The default app is refused by the catalog.</summary>
    public async Task<IActionResult> OnPostDeleteAppAsync([FromForm] string client, [FromForm] string slug, CancellationToken ct)
    {
        bool removed;
        try
        {
            removed = await _catalog.DeleteAppAsync(client, slug, ct).ConfigureAwait(false);
        }
        catch (RSCCatalogException ex)
        {
            await _audit.RecordAsync(HttpContext, "app.delete", success: false, target: $"{client}/{slug}", details: ex.Message).ConfigureAwait(false);
            return ErrApp(ex.Message, client);
        }
        if (!removed) return ErrApp($"app not found: {slug}", client);

        var purge = _purger.PurgeAppData(client, slug);
        await _audit.RecordAsync(HttpContext, "app.delete", success: true, target: $"{client}/{slug}",
            details: $"dataExisted={purge.DirectoryExisted} dataRemoved={purge.DirectoryRemoved}{(purge.Error is null ? "" : $" error={purge.Error}")}")
            .ConfigureAwait(false);

        if (!purge.Succeeded)
            return ErrApp($"deleted app '{slug}', but on-disk data removal FAILED: {purge.Error}. Clean it up manually at {purge.Path}.", client);
        var dataNote = purge.DirectoryExisted ? "data removed" : "no data on disk";
        return OkApp($"permanently deleted app '{slug}' — {dataNote}.", client);
    }

    // ---------------- Managed API keys (merged from the former /Maintenance block) ----------------

    public async Task<IActionResult> OnPostCreateApiKeyAsync(
        string? role, string? label, int? expiresInDays, int? rateLimitPerMinute, CancellationToken ct)
    {
        // This section mints ADMIN operator keys only (unbound, read+write across all clients). A
        // client's own (bound, scoped) key is minted from the Clients section's "issue key" button,
        // where the client binding is known — a client key can't be minted here without one.
        var normalizedRole = string.IsNullOrWhiteSpace(role) ? RSCApiKeyRoles.Admin : role.Trim().ToLowerInvariant();
        if (!RSCApiKeyRoles.IsValid(normalizedRole)) return Err($"Invalid role '{role}'.");
        if (normalizedRole != RSCApiKeyRoles.Admin)
            return Err("Only admin keys are minted here. Mint a client key from that client's “issue key” button.");

        DateTimeOffset? expiresAt = expiresInDays is { } d && d > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null;
        int? limit = rateLimitPerMinute is { } r && r > 0 ? r : null;
        var cleanLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        var actor = User.Identity?.Name is { Length: > 0 } name ? name : "operator";

        try
        {
            var created = await _keys.CreateAsync(normalizedRole, cleanLabel, expiresAt, limit, actor, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "apikey.create", success: true, target: created.Metadata.Id,
                details: $"role={normalizedRole} expires={expiresAt?.ToString("O") ?? "never"} rate={limit?.ToString() ?? "default"}").ConfigureAwait(false);
            return OkWithManagedKey($"created {normalizedRole} key {created.Metadata.Id} — copy the secret now; it won't be shown again.",
                created.Metadata.Id, created.PlaintextKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API key creation failed");
            await _audit.RecordAsync(HttpContext, "apikey.create", success: false, details: ex.Message).ConfigureAwait(false);
            return Err("Key creation failed — see logs.");
        }
    }

    public async Task<IActionResult> OnPostRevokeApiKeyAsync(string id, CancellationToken ct)
    {
        try
        {
            var revoked = await _keys.RevokeAsync(id, User.Identity?.Name ?? "operator", ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "apikey.revoke", success: revoked, target: id).ConfigureAwait(false);
            return revoked ? Ok($"revoked key {id}.") : Err($"no active key {id} to revoke.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API key revocation failed for {Id}", id);
            await _audit.RecordAsync(HttpContext, "apikey.revoke", success: false, target: id, details: ex.Message).ConfigureAwait(false);
            return Err("Revocation failed — see logs.");
        }
    }

    // ---------------- helpers ----------------

    private async Task<string?> MintClientKeyAsync(string clientSlug, CancellationToken ct)
    {
        try
        {
            var actor = User?.Identity?.Name ?? "operator";
            var created = await _keys.CreateAsync(
                RSCApiKeyRoles.Client, label: $"client:{clientSlug}", expiresAt: null,
                rateLimitPerMinute: null, createdBy: actor, ct, clientId: clientSlug).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "apikey.create", success: true, target: created.Metadata.Id,
                details: $"client={clientSlug} role=client").ConfigureAwait(false);
            return created.PlaintextKey;
        }
        catch (InvalidOperationException) { return null; }
    }

    private IActionResult OkWithClientKey(string flash, string clientSlug, string plaintextKey)
    {
        TempData["NewKey"] = plaintextKey;
        TempData["NewKeyClient"] = clientSlug;
        return RedirectToPage(new { flash, kind = "ok" });
    }

    private IActionResult OkWithManagedKey(string flash, string keyId, string plaintextKey)
    {
        TempData["NewManagedKey"] = plaintextKey;
        TempData["NewManagedKeyId"] = keyId;
        return RedirectToPage(new { flash, kind = "ok" });
    }

    // Client/key ops redirect to the bare page; app ops preserve ?client so the Apps section stays open.
    private IActionResult Ok(string flash) => RedirectToPage(new { flash, kind = "ok" });
    private IActionResult Err(string flash) => RedirectToPage(new { flash, kind = "err" });
    private IActionResult OkApp(string flash, string? client) => RedirectToPage(new { client, flash, kind = "ok" });
    private IActionResult ErrApp(string flash, string? client) => RedirectToPage(new { client, flash, kind = "err" });
}
