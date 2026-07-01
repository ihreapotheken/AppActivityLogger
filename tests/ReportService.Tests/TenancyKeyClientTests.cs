using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Storage.ApiKeys;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers the key-as-client-identity model: a batch's tenancy client comes from the authenticated
/// API key (not the body), and a client uses that key to self-manage its apps via /api/v2/apps.
/// </summary>
public class TenancyKeyClientTests
{
    private const string EventsUrl = "/api/v2/analytics/events";
    private const string AppsUrl = "/api/v2/apps";

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

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object MakeBatch(string? appId, string? environment, string? clientId, string eventId) => new
    {
        schemaVersion = 1,
        batchId = Guid.NewGuid().ToString(),
        platform = "android",
        sdkVersion = "1.0.0",
        hostAppVersion = "5.6.7",
        anonymousId = "anon-1",
        clientId,
        appId,
        environment,
        generatedAt = DateTimeOffset.UtcNow.ToString("O"),
        events = new[]
        {
            new
            {
                eventId, sessionId = "session-1", sequence = 0L,
                occurredAt = DateTimeOffset.UtcNow.ToString("O"),
                type = "screen", name = "home", screen = "home", feature = (string?)null,
                durationMs = 100L, properties = new Dictionary<string, string>(), items = Array.Empty<object>(),
            }
        },
    };

    [Fact]
    public async Task Client_bound_key_attributes_the_batch_to_its_client_ignoring_the_body()
    {
        await using var app = NewApp();
        var clientKey = await MintClientKeyAsync(app, "pharmacy-42");

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", clientKey);
        // Body lies about the client; the key's binding must win.
        var req = new HttpRequestMessage(HttpMethod.Post, EventsUrl)
        {
            Content = Body(MakeBatch("app-a", "production", clientId: "some-other-client", eventId: "evt-key"))
        };
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.False(receipt!.BatchRejected);
        Assert.Equal(1, receipt.AcceptedCount);

        // Database-per-app: the event lands in the KEY's client's app DB (pharmacy-42/app-a), not a
        // DB for the body-declared "some-other-client".
        var factory = app.Services.GetRequiredService<RSCIAnalyticsStoreFactory>();
        var underKeyClient = await factory.Get("pharmacy-42", "app-a").SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0,
            AppId: "app-a", Environment: "production", ClientId: "pharmacy-42"), default);
        Assert.Equal(1, underKeyClient.Total);
        Assert.All(underKeyClient.Rows, r => Assert.Equal("pharmacy-42", r.ClientId));

        // Nothing landed under the body-declared client's app DB.
        var underBodyClient = await factory.Get("some-other-client", "app-a").SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0), default);
        Assert.Equal(0, underBodyClient.Total);
    }

    [Fact]
    public async Task Client_key_can_register_and_list_its_apps()
    {
        await using var app = NewApp();
        var clientKey = await MintClientKeyAsync(app, "pharmacy-42");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", clientKey);

        // Register a new app for this client. Environment is folded into the slug (e.g. app-new-prod).
        var create = await client.PostAsync(AppsUrl, Body(new { slug = "app-new", displayName = "New App" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // It now shows for this client (alongside the seeded app-a).
        var list = await client.GetStringAsync(AppsUrl);
        Assert.Contains("app-new", list);
        Assert.Contains("app-a", list);

        // And it's a valid attribution target under this client.
        var catalog = app.Services.GetRequiredService<ReportService.Storage.Catalog.RSCICatalog>();
        Assert.True(catalog.IsValidApp("pharmacy-42", "app-new"));
        // The app is NOT visible under another client.
        Assert.False(catalog.IsValidApp("default", "app-new"));
    }

    [Fact]
    public async Task Unbound_root_key_cannot_use_the_app_api()
    {
        await using var app = NewApp();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey); // root, no client binding

        var res = await client.GetAsync(AppsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
