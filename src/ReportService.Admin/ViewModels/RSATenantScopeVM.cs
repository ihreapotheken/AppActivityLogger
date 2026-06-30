using ReportService.Analytics;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Model for the shared <c>_TenantScope</c> partial — the app / environment / client dropdown
/// selector rendered on the analytics pages alongside the platform tabs. Apps are dynamic
/// (admin-created) so this is a dropdown, not a fixed tab list. Each axis is independent; the empty
/// value means "all". <paramref name="PageName"/> is the owning page's route so the GET form posts
/// back to the right dashboard; <paramref name="CurrentPlatform"/> is round-tripped via a hidden
/// field so changing app/env/client preserves the active platform tab.
/// </summary>
public sealed record RSATenantScopeVM(
    string PageName,
    string? CurrentApp,
    string? CurrentEnv,
    string? CurrentClient,
    string? CurrentPlatform,
    IReadOnlyList<RSATenantAppOptionVM> Apps,
    IReadOnlyList<RSATenantClientOptionVM> Clients);

/// <summary>One app option for the selector, with its owning client and declared environments (used
/// to scope the app + environment dropdowns to the chosen client/app client-side).</summary>
public sealed record RSATenantAppOptionVM(string ClientSlug, string Slug, string DisplayName, IReadOnlyList<string> Environments);

/// <summary>One client option for the selector.</summary>
public sealed record RSATenantClientOptionVM(string Slug, string DisplayName);

/// <summary>Helpers for turning bound query strings into a canonical <see cref="RSCAnalyticsScope"/>.</summary>
public static class RSATenantScopes
{
    /// <summary>Trim + lowercase; null/blank ⇒ null ("all" for that axis).</summary>
    public static string? Norm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Build a scope from the four bound axes (any of which may be null/blank = all).</summary>
    public static RSCAnalyticsScope Build(string? app, string? env, string? client, string? platform) =>
        new(Norm(app), Norm(env), Norm(client), Norm(platform));

    /// <summary>Builds the selector VM from the active (non-archived) catalog entries plus the
    /// current selections, for rendering the <c>_TenantScope</c> partial. When
    /// <paramref name="restrictToClient"/> is set (a client login viewing its own dashboards) only
    /// that client and its apps are offered; otherwise (admin) every client and every app is listed
    /// and the app dropdown is filtered to the chosen client client-side via each option's
    /// <see cref="RSATenantAppOptionVM.ClientSlug"/>.</summary>
    public static async Task<RSATenantScopeVM> BuildVmAsync(
        RSCICatalog catalog, string pageName,
        string? app, string? env, string? client, string? platform, CancellationToken ct,
        string? restrictToClient = null)
    {
        var scopedClient = Norm(restrictToClient);
        var apps = scopedClient is null
            ? await catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false)
            : await catalog.ListAppsAsync(scopedClient, includeArchived: false, ct).ConfigureAwait(false);
        var allClients = await catalog.ListClientsAsync(includeArchived: false, ct).ConfigureAwait(false);
        var clients = scopedClient is null
            ? allClients
            : allClients.Where(c => string.Equals(c.Slug, scopedClient, StringComparison.Ordinal)).ToList();

        return new RSATenantScopeVM(
            PageName: pageName,
            CurrentApp: Norm(app),
            CurrentEnv: Norm(env),
            CurrentClient: scopedClient ?? Norm(client),
            CurrentPlatform: Norm(platform),
            Apps: apps.Select(a => new RSATenantAppOptionVM(a.ClientSlug, a.Slug, a.DisplayName, a.Environments)).ToList(),
            Clients: clients.Select(c => new RSATenantClientOptionVM(c.Slug, c.DisplayName)).ToList());
    }
}
