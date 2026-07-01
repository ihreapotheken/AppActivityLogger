using Microsoft.AspNetCore.Mvc;
using ReportService.Security;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.ViewComponents;

/// <summary>
/// Header tenant/app switcher that replaces the static environment chip. It drives the per-app
/// dashboard scope globally:
/// <list type="bullet">
///   <item><b>Operator</b> (cookie principal with no client claim) — every client and its apps,
///         grouped by client.</item>
///   <item><b>Client login</b> (cookie principal carrying a <c>rsc:client_id</c> claim) — only that
///         client's own apps; the client axis is fixed, so a client can never pick another tenant.</item>
/// </list>
/// Selecting an app navigates to that app's dashboard (<c>/Analytics?client=..&amp;app=..</c>).
/// </summary>
[ViewComponent(Name = "TenantSwitcher")]
public sealed class RSATenantSwitcherViewComponent : ViewComponent
{
    private readonly RSCICatalog _catalog;

    public RSATenantSwitcherViewComponent(RSCICatalog catalog) => _catalog = catalog;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var clientLogin = Norm(HttpContext.User?.FindFirst(RSCTenantClaims.ClientId)?.Value);
        var isClientLogin = clientLogin is not null;

        var currentClient = Norm(Request.Query["client"]);
        var currentApp = Norm(Request.Query["app"]);

        // A client login only ever sees its own apps; an operator sees every client's apps.
        var apps = isClientLogin
            ? await _catalog.ListAppsAsync(clientLogin!, includeArchived: false, ct).ConfigureAwait(false)
            : await _catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false);

        var appsByClient = apps
            .GroupBy(a => a.ClientSlug, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Slug, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        // Drive the groups off the CLIENT list, not the app list, so a client with zero apps still
        // shows up (with a "No registered apps" caption) instead of vanishing from the switcher. A
        // client login only lists its own client.
        var clients = (await _catalog.ListClientsAsync(includeArchived: false, ct).ConfigureAwait(false))
            .Where(c => !isClientLogin || string.Equals(c.Slug, clientLogin, StringComparison.Ordinal))
            .OrderBy(c => c.Slug, StringComparer.Ordinal);

        var groups = clients
            .Select(c => new RSATenantSwitcherGroup(
                ClientSlug: c.Slug,
                ClientDisplay: c.DisplayName,
                // A whole-client selection: "client|" (empty app part) ⇒ scope to this client, all its
                // apps. /Scope + the fan-out store already treat a null app as "all apps of the client".
                ClientValue: $"{c.Slug}|",
                ClientSelected: string.Equals(c.Slug, currentClient, StringComparison.Ordinal) && currentApp is null,
                Apps: (appsByClient.TryGetValue(c.Slug, out var la) ? la : new())
                    .Select(a => new RSATenantSwitcherApp(
                        AppSlug: a.Slug,
                        AppDisplay: a.DisplayName,
                        // The switcher POSTs this opaque "client|app" selection to /Scope, which sets the
                        // sticky scope cookie and redirects back to the current page.
                        Value: $"{a.ClientSlug}|{a.Slug}",
                        Selected: string.Equals(a.ClientSlug, currentClient, StringComparison.Ordinal)
                                  && string.Equals(a.Slug, currentApp, StringComparison.Ordinal))).ToList()))
            .ToList();

        // Return to the current page (path only — the new scope arrives via the cookie, not the query).
        var returnUrl = Request.Path.HasValue ? Request.Path.Value! : "/Analytics";

        var vm = new RSATenantSwitcherVM(
            IsClientLogin: isClientLogin,
            // "All" is selected only when neither axis is pinned. A client-level selection (client set,
            // app null) is its own state, so it no longer counts as "all".
            AllSelected: currentClient is null && currentApp is null,
            ReturnUrl: returnUrl,
            Groups: groups);
        return View(vm);
    }

    private static string? Norm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

/// <summary>View model for the header tenant/app switcher.</summary>
public sealed record RSATenantSwitcherVM(
    bool IsClientLogin,
    bool AllSelected,
    string ReturnUrl,
    IReadOnlyList<RSATenantSwitcherGroup> Groups);

public sealed record RSATenantSwitcherGroup(
    string ClientSlug,
    string ClientDisplay,
    string ClientValue,
    bool ClientSelected,
    IReadOnlyList<RSATenantSwitcherApp> Apps);

public sealed record RSATenantSwitcherApp(string AppSlug, string AppDisplay, string Value, bool Selected);
