using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.ApiKeys;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Database-per-app for problem reports: a submission is attributed to the client its API key is bound
/// to + the app from the X-Report-App header, and stored under that app's own report tree (FileSystem
/// mode). Unknown app/client is rejected, mirroring analytics.
/// </summary>
public class ReportTenancyTests
{
    private const string Url = "/api/v1/reports";

    private static IngestionAppFactory NewApp()
    {
        var app = new IngestionAppFactory();
        app.ConfigureCatalog = o => o with
        {
            SeedClients = new[] { new RSCCatalogClientSeed { Slug = "pharmacy-42", DisplayName = "Pharmacy 42" } },
            SeedApps = new[]
            {
                new RSCCatalogAppSeed { ClientSlug = "pharmacy-42", Slug = "app-a", DisplayName = "App A" },
            },
        };
        return app;
    }

    private static async Task<string> MintClientKeyAsync(IngestionAppFactory app, string clientSlug)
    {
        var store = app.Services.GetRequiredService<RSCIApiKeyStore>();
        var created = await store.CreateAsync(RSCApiKeyRoles.Client, "test-client", null, null, "test", default, clientId: clientSlug);
        return created.PlaintextKey;
    }

    private static StringContent Body() =>
        new("{\"platform\":\"Android\",\"message\":\"per-app report\",\"pharmacyId\":\"DE-1\"}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Report_is_stored_under_the_key_client_and_X_Report_App()
    {
        await using var app = NewApp();
        var clientKey = await MintClientKeyAsync(app, "pharmacy-42");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", clientKey);

        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = Body() };
        req.Headers.Add("X-Report-App", "app-a");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var factory = app.Services.GetRequiredService<RSCIReportStoreFactory>();
        // Landed under pharmacy-42/app-a's own tree…
        Assert.Single(factory.Get("pharmacy-42", "app-a").List("android"));
        // …and not under the default app.
        Assert.Empty(factory.Get("default", "default").List("android"));
    }

    [Fact]
    public async Task Unknown_app_is_rejected()
    {
        await using var app = NewApp();
        var clientKey = await MintClientKeyAsync(app, "pharmacy-42");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", clientKey);

        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = Body() };
        req.Headers.Add("X-Report-App", "ghost-app"); // not registered for pharmacy-42
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
