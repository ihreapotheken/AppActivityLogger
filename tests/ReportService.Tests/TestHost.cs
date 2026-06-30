using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReportService.Options;

namespace ReportService.Tests;

/// <summary>Boots the real ingestion service in-process with an isolated temp <c>ReportsRoot</c> and auth-abuse DB. Per-test tweaks via <see cref="Configure"/>.</summary>
public class IngestionAppFactory : WebApplicationFactory<ReportService.IngestionProgram>
{
    public string ReportsRoot { get; } = Path.Combine(Path.GetTempPath(), $"rs-tests-{Guid.NewGuid():N}");
    public string AbuseDbPath { get; } = Path.Combine(Path.GetTempPath(), $"rs-tests-abuse-{Guid.NewGuid():N}.db");

    public const string ApiKey = "test-api-key-0123456789";

    /// <summary>Transforms the default <c>RSCReportServiceOptions</c>; typically <c>baseline with { ... }</c>.</summary>
    public Func<RSCReportServiceOptions, RSCReportServiceOptions>? Configure { get; set; }

    /// <summary>Optional override for <c>RSCAnalyticsOptions</c>. The analytics DB lands under the
    /// per-test <see cref="ReportsRoot"/> so concurrent tests don't share state.</summary>
    public Func<RSCAnalyticsOptions, RSCAnalyticsOptions>? ConfigureAnalytics { get; set; }

    /// <summary>Optional override for <c>RSCCatalogOptions</c> (the tenancy catalog). Tests use this
    /// to seed apps/clients (via <c>SeedApps</c>/<c>SeedClients</c>) or to toggle validation. The
    /// catalog DB lands under the per-test <see cref="ReportsRoot"/>.</summary>
    public Func<RSCCatalogOptions, RSCCatalogOptions>? ConfigureCatalog { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureTestServices(services =>
        {
            // Replace the RSCReportServiceOptions snapshot Program.cs registered at startup. The auth
            // handler, ingestion service, rate-limit attachment point, and storage back ends all
            // resolve this singleton at request time, so this one substitution is enough to re-point
            // the test host at isolated temp files + a working ApiKey.
            var opts = new RSCReportServiceOptions
            {
                ApiKey = ApiKey,
                ReportsRoot = ReportsRoot,
                AuthAbuseDbPath = AbuseDbPath,
                Storage = "FileSystem",
                IngestConcurrency = 8,
                IngestQueueLimit = 8,
                RateLimitPermitsPerMinute = 10000
            };
            if (Configure is not null) opts = Configure(opts);

            services.RemoveAll<RSCReportServiceOptions>();
            services.AddSingleton(opts);

            var analytics = new RSCAnalyticsOptions
            {
                // Each test gets its own DB file under the per-test temp root.
                SqliteDbPath = $"analytics-{Guid.NewGuid():N}.db",
                IdentifierHashPepper = "test-pepper",
            };
            if (ConfigureAnalytics is not null) analytics = ConfigureAnalytics(analytics);

            services.RemoveAll<RSCAnalyticsOptions>();
            services.AddSingleton(analytics);

            // Per-test tenancy catalog DB under the isolated ReportsRoot. Self-seeds the default
            // app/env/client; tests add their own apps/clients via SeedApps/SeedClients.
            var catalog = new RSCCatalogOptions
            {
                SqliteDbPath = $"catalog-{Guid.NewGuid():N}.db",
            };
            if (ConfigureCatalog is not null) catalog = ConfigureCatalog(catalog);

            services.RemoveAll<RSCCatalogOptions>();
            services.AddSingleton(catalog);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(ReportsRoot, recursive: true); } catch { /* ignore */ }
        foreach (var extra in new[] { "", "-wal", "-shm" })
            try { File.Delete(AbuseDbPath + extra); } catch { /* ignore */ }
    }
}
