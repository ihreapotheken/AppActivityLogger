using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Catalog;
using Xunit;

namespace ReportService.Tests;

/// <summary>Unit tests for the SQLite tenancy catalog (clients + their apps + environments), against a
/// real per-test DB file (no mocks), mirroring the other storage suites. Apps are nested under
/// clients, so app slugs are unique only within a client.</summary>
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
    public void Default_client_app_and_env_are_self_seeded()
    {
        Assert.True(_catalog.IsValidClient("default"));
        Assert.True(_catalog.IsValidApp("default", "default"));
        Assert.True(_catalog.IsValidEnvironment("default", "default", "production"));
    }

    [Fact]
    public async Task Create_app_under_a_client_makes_app_and_its_environments_valid()
    {
        await _catalog.CreateClientAsync("pharmacy-42", "Pharmacy 42", default);
        await _catalog.CreateAppAsync("pharmacy-42", "app-a", "App A", new[] { "production", "staging" }, default);

        Assert.True(_catalog.IsValidApp("pharmacy-42", "app-a"));
        Assert.True(_catalog.IsValidEnvironment("pharmacy-42", "app-a", "production"));
        Assert.True(_catalog.IsValidEnvironment("pharmacy-42", "app-a", "staging"));
        Assert.False(_catalog.IsValidEnvironment("pharmacy-42", "app-a", "qa"));
        // The same app slug under a different client is independent (not auto-valid).
        Assert.False(_catalog.IsValidApp("default", "app-a"));
        // Normalization: client/slug/env are matched case-insensitively (stored lowercased).
        Assert.True(_catalog.IsValidEnvironment("PHARMACY-42", "APP-A", "PRODUCTION"));
    }

    [Fact]
    public async Task Two_clients_can_each_own_an_app_with_the_same_slug()
    {
        await _catalog.CreateClientAsync("client-a", "Client A", default);
        await _catalog.CreateClientAsync("client-b", "Client B", default);
        await _catalog.CreateAppAsync("client-a", "main", "Main", new[] { "production" }, default);
        await _catalog.CreateAppAsync("client-b", "main", "Main", new[] { "production" }, default);

        Assert.True(_catalog.IsValidApp("client-a", "main"));
        Assert.True(_catalog.IsValidApp("client-b", "main"));
    }

    [Fact]
    public async Task Duplicate_app_slug_within_one_client_throws()
    {
        await _catalog.CreateClientAsync("client-a", "Client A", default);
        await _catalog.CreateAppAsync("client-a", "app-a", "App A", new[] { "production" }, default);
        await Assert.ThrowsAsync<RSCCatalogException>(() =>
            _catalog.CreateAppAsync("client-a", "app-a", "Dup", new[] { "production" }, default));
    }

    [Fact]
    public async Task Creating_an_app_under_an_unknown_client_throws()
    {
        await Assert.ThrowsAsync<RSCCatalogException>(() =>
            _catalog.CreateAppAsync("ghost-client", "app-a", "App A", new[] { "production" }, default));
    }

    [Fact]
    public async Task Invalid_slug_throws()
    {
        await Assert.ThrowsAsync<RSCCatalogException>(() =>
            _catalog.CreateAppAsync("default", "Not A Slug!", "x", new[] { "production" }, default));
    }

    [Fact]
    public async Task Archiving_an_app_makes_it_invalid()
    {
        await _catalog.CreateAppAsync("default", "app-a", "App A", new[] { "production" }, default);
        Assert.True(_catalog.IsValidApp("default", "app-a"));

        Assert.True(await _catalog.ArchiveAppAsync("default", "app-a", default));
        Assert.False(_catalog.IsValidApp("default", "app-a"));
    }

    [Fact]
    public async Task Cannot_remove_an_apps_last_environment()
    {
        await _catalog.CreateAppAsync("default", "app-a", "App A", new[] { "production" }, default);
        Assert.False(await _catalog.RemoveEnvironmentAsync("default", "app-a", "production", default));
        Assert.True(_catalog.IsValidEnvironment("default", "app-a", "production"));
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
    public void Config_seed_clients_and_their_apps_are_present_at_bootstrap()
    {
        // Seeding happens synchronously in the RSCSqliteCatalog constructor (bootstrap), so this
        // test has nothing to await — it's a plain synchronous [Fact]. Clients seed before apps, so a
        // seed app can name its owning client.
        var seeded = new RSCCatalogOptions
        {
            SqliteDbPath = "catalog-seeded.db",
            SeedClients = new[] { new RSCCatalogClientSeed { Slug = "client-x", DisplayName = "Client X" } },
            SeedApps = new[] { new RSCCatalogAppSeed { ClientSlug = "client-x", Slug = "app-x", DisplayName = "App X", Environments = new[] { "production" } } },
        };
        var catalog = new RSCSqliteCatalog(
            new RSCReportServiceOptions { ReportsRoot = _root }, seeded, new RSCComponentHealth(),
            NullLogger<RSCSqliteCatalog>.Instance);

        Assert.True(catalog.IsValidClient("client-x"));
        Assert.True(catalog.IsValidApp("client-x", "app-x"));
        Assert.True(catalog.IsValidEnvironment("client-x", "app-x", "production"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
