using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Catalog;
using Xunit;

namespace ReportService.Tests;

/// <summary>Unit tests for the SQLite tenancy catalog (clients + their apps), against a real per-test
/// DB file (no mocks), mirroring the other storage suites. Apps are nested under clients, so app slugs
/// are unique only within a client. Environment is folded into the app slug (a client creates a
/// separate app per environment, e.g. app-a-qa / app-a-prod) — there is no separate environment
/// axis.</summary>
public sealed class CatalogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-catalog-tests-{Guid.NewGuid():N}");
    private readonly RSCCatalogOptions _options;
    private readonly RSCSqliteCatalog _catalog;

    public CatalogStoreTests()
    {
        Directory.CreateDirectory(_root);
        _options = new RSCCatalogOptions { SqliteDbPath = "catalog.db" };
        _catalog = NewCatalog();
    }

    private RSCSqliteCatalog NewCatalog() => new(
        new RSCReportServiceOptions { ReportsRoot = _root },
        _options,
        new RSCComponentHealth(),
        NullLogger<RSCSqliteCatalog>.Instance);

    [Fact]
    public void Default_client_and_app_are_self_seeded()
    {
        Assert.True(_catalog.IsValidClient("default"));
        Assert.True(_catalog.IsValidApp("default", "default"));
    }

    [Fact]
    public async Task Create_app_under_a_client_makes_app_valid()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-a", "App A", default);

        Assert.True(_catalog.IsValidApp("pharmacy-42", "app-a"));
        // The same app slug under a different client is independent (not auto-valid).
        Assert.False(_catalog.IsValidApp("default", "app-a"));
        // Normalization: client/slug are matched case-insensitively (stored lowercased).
        Assert.True(_catalog.IsValidApp("PHARMACY-42", "APP-A"));
    }

    [Fact]
    public async Task Two_clients_can_each_own_an_app_with_the_same_slug()
    {
        await _catalog.CreateClientAsync("client-a", "Client A", default);
        await _catalog.CreateClientAsync("client-b", "Client B", default);
        await _catalog.CreateAppAsync("client-a", "main", "Main", default);
        await _catalog.CreateAppAsync("client-b", "main", "Main", default);

        Assert.True(_catalog.IsValidApp("client-a", "main"));
        Assert.True(_catalog.IsValidApp("client-b", "main"));
    }

    [Fact]
    public async Task Duplicate_app_slug_within_one_client_throws()
    {
        await _catalog.CreateClientAsync("client-a", "Client A", default);
        await _catalog.CreateAppAsync("client-a", "app-a", "App A", default);
        await Assert.ThrowsAsync<RSCCatalogException>(() =>
            _catalog.CreateAppAsync("client-a", "app-a", "Dup", default));
    }

    [Fact]
    public async Task Creating_an_app_under_an_unknown_client_throws()
    {
        await Assert.ThrowsAsync<RSCCatalogException>(() =>
            _catalog.CreateAppAsync("ghost-client", "app-a", "App A", default));
    }

    [Fact]
    public async Task Invalid_slug_throws()
    {
        await Assert.ThrowsAsync<RSCCatalogException>(() =>
            _catalog.CreateAppAsync("default", "Not A Slug!", "x", default));
    }

    [Fact]
    public async Task Archiving_an_app_makes_it_invalid()
    {
        await _catalog.CreateAppAsync("default", "app-a", "App A", default);
        Assert.True(_catalog.IsValidApp("default", "app-a"));

        Assert.True(await _catalog.ArchiveAppAsync("default", "app-a", default));
        Assert.False(_catalog.IsValidApp("default", "app-a"));
    }

    [Fact]
    public async Task Client_registration_and_archive()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        Assert.True(_catalog.IsValidClient("pharmacy-42"));
        Assert.False(_catalog.IsValidClient("ghost"));

        Assert.True(await _catalog.ArchiveClientAsync("pharmacy-42", default));
        Assert.False(_catalog.IsValidClient("pharmacy-42"));
    }

    [Fact]
    public async Task Archiving_a_client_hides_its_apps_from_validation_and_active_listing()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-a", "App A", default);
        Assert.True(_catalog.IsValidApp("pharmacy-42", "app-a"));

        Assert.True(await _catalog.ArchiveClientAsync("pharmacy-42", default));

        // The app row is untouched (still archived_at IS NULL), but it's invalid + hidden because its
        // owning client is archived.
        Assert.False(_catalog.IsValidApp("pharmacy-42", "app-a"));
        var active = await _catalog.ListAllAppsAsync(includeArchived: false, default);
        Assert.DoesNotContain(active, a => a.ClientSlug == "pharmacy-42");
        // The admin view (includeArchived: true) still sees it, so it remains manageable.
        var all = await _catalog.ListAllAppsAsync(includeArchived: true, default);
        Assert.Contains(all, a => a.ClientSlug == "pharmacy-42" && a.Slug == "app-a");
    }

    [Fact]
    public async Task Unarchiving_a_client_restores_its_apps_to_prior_state()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-a", "App A", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-b", "App B", default);
        // app-b was individually archived before the client was archived.
        await _catalog.ArchiveAppAsync("pharmacy-42", "app-b", default);
        await _catalog.ArchiveClientAsync("pharmacy-42", default);

        Assert.True(await _catalog.UnarchiveClientAsync("pharmacy-42", default));

        Assert.True(_catalog.IsValidClient("pharmacy-42"));
        Assert.True(_catalog.IsValidApp("pharmacy-42", "app-a"));   // was active → active again
        Assert.False(_catalog.IsValidApp("pharmacy-42", "app-b"));  // was archived → stays archived
    }

    [Fact]
    public async Task Deleting_a_client_removes_it_and_all_its_apps()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-a", "App A", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-b", "App B", default);

        Assert.True(await _catalog.DeleteClientAsync("pharmacy-42", default));

        Assert.False(_catalog.IsValidClient("pharmacy-42"));
        Assert.False(_catalog.IsValidApp("pharmacy-42", "app-a"));
        Assert.Null(await _catalog.GetClientAsync("pharmacy-42", default));
        // Hard delete (not archive): the rows are gone from the admin view too, and the slug frees up.
        var clients = await _catalog.ListClientsAsync(includeArchived: true, default);
        Assert.DoesNotContain(clients, c => c.Slug == "pharmacy-42");
        var apps = await _catalog.ListAppsAsync("pharmacy-42", includeArchived: true, default);
        Assert.Empty(apps);
        // The freed slug can be re-registered.
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42 (new)", default);
        Assert.True(_catalog.IsValidClient("pharmacy-42"));
    }

    [Fact]
    public async Task Deleting_the_default_client_is_refused()
    {
        await Assert.ThrowsAsync<RSCCatalogException>(() => _catalog.DeleteClientAsync("default", default));
        Assert.True(_catalog.IsValidClient("default"));
    }

    [Fact]
    public async Task Deleting_an_unknown_client_returns_false()
    {
        Assert.False(await _catalog.DeleteClientAsync("ghost", default));
    }

    [Fact]
    public async Task Deleting_an_app_removes_it_but_not_its_sibling()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-a", "App A", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-b", "App B", default);

        Assert.True(await _catalog.DeleteAppAsync("pharmacy-42", "app-a", default));

        Assert.False(_catalog.IsValidApp("pharmacy-42", "app-a"));
        Assert.True(_catalog.IsValidApp("pharmacy-42", "app-b"));
        Assert.Null(await _catalog.GetAppAsync("pharmacy-42", "app-a", default));
    }

    [Fact]
    public async Task Deleting_or_archiving_the_default_app_is_refused()
    {
        await Assert.ThrowsAsync<RSCCatalogException>(() => _catalog.DeleteAppAsync("default", "default", default));
        await Assert.ThrowsAsync<RSCCatalogException>(() => _catalog.ArchiveAppAsync("default", "default", default));
        Assert.True(_catalog.IsValidApp("default", "default"));
    }

    [Fact]
    public void Config_seed_clients_and_their_apps_are_present_at_bootstrap()
    {
        // Seeding happens synchronously in the RSCSqliteCatalog constructor (bootstrap), so this
        // test has nothing to await — it's a plain synchronous [Fact]. Clients seed before apps, so a
        // seed app can name its owning client.
        var seeded = new RSCCatalogOptions
        {
            SqliteDbPath = "catalog-seeded.db",
            SeedClients = new[] { new RSCCatalogClientSeed { Slug = "client-x", DisplayName = "Client X" } },
            SeedApps = new[] { new RSCCatalogAppSeed { ClientSlug = "client-x", Slug = "app-x", DisplayName = "App X" } },
        };
        var catalog = new RSCSqliteCatalog(
            new RSCReportServiceOptions { ReportsRoot = _root }, seeded, new RSCComponentHealth(),
            NullLogger<RSCSqliteCatalog>.Instance);

        Assert.True(catalog.IsValidClient("client-x"));
        Assert.True(catalog.IsValidApp("client-x", "app-x"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
