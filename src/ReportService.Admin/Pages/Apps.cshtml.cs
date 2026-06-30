using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

/// <summary>
/// Operator console for the tenancy catalog's <b>apps</b>, which are owned by a client. Pick a client
/// to manage its apps (create with an immutable slug + display name + initial environments, rename,
/// add/remove environments, archive); with no client selected the page lists every client's apps for
/// oversight. Clients can also self-manage their apps over the JSON API (<c>/api/v2/apps</c>) using
/// their access key; this page is the admin-side equivalent. Mutations redirect back with a
/// <c>?flash=</c> toast, preserving the selected client.
/// </summary>
public sealed class RSAAppsModel : PageModel
{
    private readonly RSCICatalog _catalog;

    public RSAAppsModel(RSCICatalog catalog) => _catalog = catalog;

    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true, Name = "edit")] public string? EditSlug { get; set; }

    public IReadOnlyList<RSCClientRecord> Clients { get; private set; } = Array.Empty<RSCClientRecord>();
    public IReadOnlyList<RSCAppRecord> Apps { get; private set; } = Array.Empty<RSCAppRecord>();

    /// <summary>The client whose apps are shown, or null when listing every client's apps.</summary>
    public string? SelectedClient { get; private set; }
    public string? Flash { get; private set; }
    public string? FlashKind { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Clients = await _catalog.ListClientsAsync(includeArchived: false, ct).ConfigureAwait(false);
        SelectedClient = string.IsNullOrWhiteSpace(Client) ? null : Client.Trim().ToLowerInvariant();

        Apps = SelectedClient is null
            ? await _catalog.ListAllAppsAsync(includeArchived: true, ct).ConfigureAwait(false)
            : await _catalog.ListAppsAsync(SelectedClient, includeArchived: true, ct).ConfigureAwait(false);

        if (Request.Query.TryGetValue("flash", out var flash))
        {
            Flash = flash.ToString();
            FlashKind = Request.Query.TryGetValue("kind", out var kind) ? kind.ToString() : "ok";
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm] string? client, [FromForm] string? slug, [FromForm] string? displayName,
        [FromForm] string? environments, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client)) return Err("Select a client to create the app under.", client);
        var envs = SplitEnvironments(environments);
        if (envs.Count == 0) return Err("Enter at least one environment (e.g. production, staging).", client);
        try
        {
            var app = await _catalog.CreateAppAsync(client, slug ?? string.Empty, displayName ?? string.Empty, envs, ct)
                .ConfigureAwait(false);
            return Ok($"created app '{app.Slug}' for client '{app.ClientSlug}'", client);
        }
        catch (RSCCatalogException ex) { return Err(ex.Message, client); }
    }

    public async Task<IActionResult> OnPostRenameAsync(
        [FromForm] string client, [FromForm] string slug, [FromForm] string? displayName, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.RenameAppAsync(client, slug, displayName ?? string.Empty, ct).ConfigureAwait(false);
            return ok ? Ok($"renamed '{slug}'", client) : Err($"app not found: {slug}", client);
        }
        catch (RSCCatalogException ex) { return Err(ex.Message, client); }
    }

    public async Task<IActionResult> OnPostAddEnvAsync(
        [FromForm] string client, [FromForm] string slug, [FromForm] string? environment, CancellationToken ct)
    {
        try
        {
            var ok = await _catalog.AddEnvironmentAsync(client, slug, environment ?? string.Empty, ct).ConfigureAwait(false);
            return ok ? Ok($"added environment to '{slug}'", client) : Err($"app not found: {slug}", client);
        }
        catch (RSCCatalogException ex) { return Err(ex.Message, client); }
    }

    public async Task<IActionResult> OnPostRemoveEnvAsync(
        [FromForm] string client, [FromForm] string slug, [FromForm] string environment, CancellationToken ct)
    {
        var ok = await _catalog.RemoveEnvironmentAsync(client, slug, environment, ct).ConfigureAwait(false);
        return ok ? Ok($"removed '{environment}' from '{slug}'", client)
                  : Err($"could not remove '{environment}' (an app must keep at least one environment).", client);
    }

    public async Task<IActionResult> OnPostArchiveAsync([FromForm] string client, [FromForm] string slug, CancellationToken ct)
    {
        var ok = await _catalog.ArchiveAppAsync(client, slug, ct).ConfigureAwait(false);
        return ok ? Ok($"archived '{slug}'", client) : Err($"app not found / already archived: {slug}", client);
    }

    private static List<string> SplitEnvironments(string? raw) =>
        (raw ?? string.Empty)
            .Split(new[] { ',', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private IActionResult Ok(string flash, string? client) => RedirectToPage(new { client, flash, kind = "ok" });
    private IActionResult Err(string flash, string? client) => RedirectToPage(new { client, flash, kind = "err" });
}
