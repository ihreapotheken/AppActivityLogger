using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Catalog;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// The on-disk half of a hard delete: <see cref="RSCIClientDataPurger"/> wipes the per-app data trees a
/// client (or one app) owns under <c>{ReportsRoot}/apps/{client}[/{app}]/</c> and releases the cached
/// store handles, against the real factories (no mocks). Built standalone (no web host) so background
/// workers can't re-provision a tree mid-assert; the catalog-row half is covered by
/// <see cref="CatalogStoreTests"/>.
/// </summary>
public sealed class ClientDataPurgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-purge-tests-{Guid.NewGuid():N}");
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly RSCSqliteAnalyticsStoreFactory _analytics;
    private readonly RSCReportStoreFactory _reports;
    private readonly RSCSqliteCatalog _catalog;
    private readonly RSCClientDataPurger _purger;

    public ClientDataPurgerTests()
    {
        Directory.CreateDirectory(_root);
        _reportOptions = new RSCReportServiceOptions { ReportsRoot = _root };
        var catalogOptions = new RSCCatalogOptions { SqliteDbPath = "catalog.db" };
        _analytics = new RSCSqliteAnalyticsStoreFactory(
            _reportOptions, new RSCAnalyticsOptions(), catalogOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        _reports = new RSCReportStoreFactory(_reportOptions, catalogOptions, NullLoggerFactory.Instance);
        _catalog = new RSCSqliteCatalog(_reportOptions, catalogOptions, new RSCComponentHealth(), NullLogger<RSCSqliteCatalog>.Instance);
        _purger = new RSCClientDataPurger(_reportOptions, _analytics, _reports, NullLogger<RSCClientDataPurger>.Instance);
    }

    /// <summary>Register the apps + touch their stores so their on-disk DBs + trees are provisioned.</summary>
    private async Task SeedAndProvisionAsync(params (string Client, string App)[] apps)
    {
        foreach (var (client, appSlug) in apps)
        {
            if (!_catalog.IsValidClient(client)) await _catalog.CreateClientAsync(client, client, default);
            if (!_catalog.IsValidApp(client, appSlug)) await _catalog.CreateAppAsync(client, appSlug, appSlug, default);
            _ = _analytics.Get(client, appSlug);
            _ = _reports.Get(client, appSlug);
        }
    }

    private string AppDir(string client, string? appSlug = null) =>
        appSlug is null
            ? Path.Combine(_root, "apps", client)
            : Path.Combine(_root, "apps", client, appSlug);

    [Fact]
    public async Task Purging_a_client_deletes_its_whole_tree_and_leaves_other_clients()
    {
        await SeedAndProvisionAsync(("pharmacy-42", "app-a"), ("pharmacy-42", "app-b"), ("pharmacy-99", "app-x"));
        Assert.True(Directory.Exists(AppDir("pharmacy-42")));
        Assert.True(Directory.Exists(AppDir("pharmacy-99")));

        // Mirror the page handler: drop the catalog rows, then purge the bytes.
        Assert.True(await _catalog.DeleteClientAsync("pharmacy-42", default));
        var result = _purger.PurgeClientData("pharmacy-42");

        Assert.True(result.Succeeded);
        Assert.True(result.DirectoryExisted);
        Assert.True(result.DirectoryRemoved);
        Assert.False(Directory.Exists(AppDir("pharmacy-42")));   // gone
        Assert.True(Directory.Exists(AppDir("pharmacy-99")));    // untouched
        Assert.False(_catalog.IsValidClient("pharmacy-42"));
    }

    [Fact]
    public async Task Purging_a_single_app_deletes_only_that_apps_tree()
    {
        await SeedAndProvisionAsync(("pharmacy-42", "app-a"), ("pharmacy-42", "app-b"));
        Assert.True(Directory.Exists(AppDir("pharmacy-42", "app-a")));
        Assert.True(Directory.Exists(AppDir("pharmacy-42", "app-b")));

        Assert.True(await _catalog.DeleteAppAsync("pharmacy-42", "app-a", default));
        var result = _purger.PurgeAppData("pharmacy-42", "app-a");

        Assert.True(result.DirectoryRemoved);
        Assert.False(Directory.Exists(AppDir("pharmacy-42", "app-a")));   // gone
        Assert.True(Directory.Exists(AppDir("pharmacy-42", "app-b")));    // sibling survives
        Assert.True(Directory.Exists(AppDir("pharmacy-42")));             // client dir survives
        Assert.False(_catalog.IsValidApp("pharmacy-42", "app-a"));
        Assert.True(_catalog.IsValidApp("pharmacy-42", "app-b"));
    }

    [Fact]
    public void Purging_a_client_with_no_data_on_disk_is_a_clean_no_op()
    {
        // A client that never received traffic has no tree on disk — purging is still a success.
        var result = _purger.PurgeClientData("never-seen");

        Assert.True(result.Succeeded);
        Assert.False(result.DirectoryExisted);
        Assert.False(result.DirectoryRemoved);
    }

    [Fact]
    public async Task Re_provisioning_after_a_purge_starts_from_an_empty_store()
    {
        await SeedAndProvisionAsync(("pharmacy-42", "app-a"));
        _purger.PurgeAppData("pharmacy-42", "app-a");
        Assert.False(Directory.Exists(AppDir("pharmacy-42", "app-a")));

        // The cached handle was evicted, so the next Get rebuilds the DB cleanly (no stale handle to a
        // deleted file).
        var store = _analytics.Get("pharmacy-42", "app-a");
        Assert.NotNull(store);
        Assert.True(Directory.Exists(AppDir("pharmacy-42", "app-a")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
