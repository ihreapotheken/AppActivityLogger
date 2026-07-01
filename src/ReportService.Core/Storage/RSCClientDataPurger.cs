using Microsoft.Extensions.Logging;
using ReportService.Analytics;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Storage;

/// <summary>Outcome of a data purge. <see cref="DirectoryExisted"/> is false when there was nothing on
/// disk (e.g. a client that never received traffic) — that is still a success.</summary>
public sealed record RSCDataPurgeResult(bool DirectoryExisted, bool DirectoryRemoved, string Path, string? Error)
{
    /// <summary>True when there is no leftover data on disk afterwards — either nothing was there or it
    /// was removed cleanly.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// The filesystem half of a hard delete in the database-per-app model: permanently removes every byte a
/// client (or a single app) owns under <c>{ReportsRoot}/apps/{client}[/{app}]/</c> — the analytics DBs,
/// the problem-report trees, the report indexes, and their WAL/backup sidecars — after evicting the
/// cached per-app store handles and releasing their pooled SQLite connections so the files aren't held
/// open. The catalog rows and API keys are removed by the caller; this owns only the bytes on disk.
/// </summary>
public interface RSCIClientDataPurger
{
    /// <summary>Evict caches + delete all on-disk data for every app of a client.</summary>
    RSCDataPurgeResult PurgeClientData(string clientSlug);

    /// <summary>Evict caches + delete all on-disk data for a single app.</summary>
    RSCDataPurgeResult PurgeAppData(string clientSlug, string appSlug);
}

public sealed class RSCClientDataPurger : RSCIClientDataPurger
{
    private readonly string _reportsRoot;
    private readonly RSCIAnalyticsStoreFactory _analyticsFactory;
    private readonly RSCIReportStoreFactory _reportFactory;
    private readonly ILogger<RSCClientDataPurger> _logger;

    public RSCClientDataPurger(
        RSCReportServiceOptions reportOptions,
        RSCIAnalyticsStoreFactory analyticsFactory,
        RSCIReportStoreFactory reportFactory,
        ILogger<RSCClientDataPurger> logger)
    {
        _reportsRoot = reportOptions.ReportsRoot;
        _analyticsFactory = analyticsFactory;
        _reportFactory = reportFactory;
        _logger = logger;
    }

    public RSCDataPurgeResult PurgeClientData(string clientSlug)
    {
        var client = RSCCatalogSlug.Normalize(clientSlug);
        if (client.Length == 0)
            return Fail(Path.Combine(_reportsRoot, "apps"), "empty client slug — refusing to purge");

        // Release every per-app handle for the client before touching the files, so no pooled SQLite
        // connection can recreate a DB we are about to delete.
        _analyticsFactory.EvictClient(client);
        _reportFactory.EvictClient(client);

        return DeleteTree(Path.Combine(_reportsRoot, "apps", client));
    }

    public RSCDataPurgeResult PurgeAppData(string clientSlug, string appSlug)
    {
        var client = RSCCatalogSlug.Normalize(clientSlug);
        var app = RSCCatalogSlug.Normalize(appSlug);
        if (client.Length == 0 || app.Length == 0)
            return Fail(Path.Combine(_reportsRoot, "apps"), "empty client/app slug — refusing to purge");

        _analyticsFactory.EvictApp(client, app);
        _reportFactory.EvictApp(client, app);

        return DeleteTree(Path.Combine(_reportsRoot, "apps", client, app));
    }

    private RSCDataPurgeResult DeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
            return new RSCDataPurgeResult(DirectoryExisted: false, DirectoryRemoved: false, Path: dir, Error: null);
        try
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Purged tenant data tree at {Path}", dir);
            return new RSCDataPurgeResult(DirectoryExisted: true, DirectoryRemoved: true, Path: dir, Error: null);
        }
        catch (Exception ex)
        {
            // The catalog rows are already gone by the time we get here, so the leftover bytes are
            // inaccessible — surface the failure so the operator can clean up out of band.
            _logger.LogError(ex, "Failed to purge tenant data tree at {Path}", dir);
            return new RSCDataPurgeResult(DirectoryExisted: true, DirectoryRemoved: false, Path: dir, Error: ex.Message);
        }
    }

    private static RSCDataPurgeResult Fail(string path, string error) =>
        new(DirectoryExisted: false, DirectoryRemoved: false, Path: path, Error: error);
}
