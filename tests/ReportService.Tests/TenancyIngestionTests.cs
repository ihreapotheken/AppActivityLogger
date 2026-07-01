using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// End-to-end coverage of the (app, client) tenancy axes on the analytics ingestion path: attribution
/// resolution (body / header / default), catalog validation, and per-tenant isolation in storage.
/// Environment is folded into the app slug — it is no longer a validated axis (the body's environment
/// value is still stamped on the vestigial column but never rejected). The catalog is seeded per-test
/// with app-a/app-b + pharmacy-42.
/// </summary>
public class TenancyIngestionTests
{
    private const string Url = "/api/v2/analytics/events";

    private static IngestionAppFactory NewApp()
    {
        var app = new IngestionAppFactory();
        app.ConfigureCatalog = o => o with
        {
            // Apps are nested under clients now, so app-a/app-b belong to pharmacy-42. The static root
            // key these tests authenticate with is unbound, so the body-declared clientId still applies.
            SeedClients = new[] { new RSCCatalogClientSeed { Slug = "pharmacy-42", DisplayName = "Pharmacy 42" } },
            SeedApps = new[]
            {
                new RSCCatalogAppSeed { ClientSlug = "pharmacy-42", Slug = "app-a", DisplayName = "App A" },
                new RSCCatalogAppSeed { ClientSlug = "pharmacy-42", Slug = "app-b", DisplayName = "App B" },
            },
        };
        return app;
    }

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object MakeBatch(string? appId, string? environment, string? clientId,
        string? batchId = null, string? eventId = null) => new
    {
        schemaVersion = 1,
        batchId = batchId ?? Guid.NewGuid().ToString(),
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
                eventId = eventId ?? Guid.NewGuid().ToString(),
                sessionId = "session-1",
                sequence = 0L,
                occurredAt = DateTimeOffset.UtcNow.ToString("O"),
                type = "screen",
                name = "home",
                screen = "home",
                feature = (string?)null,
                durationMs = 100L,
                properties = new Dictionary<string, string>(),
                items = Array.Empty<object>(),
            }
        },
    };

    private static async Task<RSCAnalyticsBatchReceipt?> PostAsync(IngestionAppFactory app, object batch,
        Action<HttpRequestMessage>? configure = null)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = Body(batch) };
        configure?.Invoke(req);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
    }

    /// <summary>Posts a batch expected to be fully rejected: asserts the standardised 400 and returns
    /// the receipt (still delivered as the response body so the reject reason is visible).</summary>
    private static async Task<RSCAnalyticsBatchReceipt?> PostRejectedAsync(IngestionAppFactory app, object batch,
        Action<HttpRequestMessage>? configure = null)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = Body(batch) };
        configure?.Invoke(req);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
    }

    [Fact]
    public async Task Registered_tenant_is_accepted_and_stored_under_that_tenant()
    {
        await using var app = NewApp();
        var receipt = await PostAsync(app, MakeBatch("app-a", "staging", "pharmacy-42", eventId: "evt-1"));
        Assert.False(receipt!.BatchRejected);
        Assert.Equal(1, receipt.AcceptedCount);

        // Database-per-app: the batch lands in pharmacy-42/app-a's own analytics.db.
        var store = app.Services.GetRequiredService<RSCIAnalyticsStoreFactory>().Get("pharmacy-42", "app-a");
        var page = await store.SearchEventsAsync(new RSCAnalyticsEventFilter(
            Platform: null, Type: null, Name: null, Screen: null, SessionId: null,
            From: null, Until: null, Limit: 100, Offset: 0,
            AppId: "app-a", Environment: "staging", ClientId: "pharmacy-42"), default);
        Assert.Equal(1, page.Total);
        Assert.All(page.Rows, r =>
        {
            Assert.Equal("app-a", r.AppId);
            Assert.Equal("staging", r.Environment);
            Assert.Equal("pharmacy-42", r.ClientId);
        });
    }

    [Fact]
    public async Task Unknown_app_is_batch_rejected()
    {
        await using var app = NewApp();
        var receipt = await PostRejectedAsync(app, MakeBatch("ghost-app", "production", "pharmacy-42"));
        Assert.True(receipt!.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.AppUnknown, receipt.BatchRejectReason);
    }

    [Fact]
    public async Task Unknown_client_is_batch_rejected()
    {
        await using var app = NewApp();
        var receipt = await PostRejectedAsync(app, MakeBatch("app-a", "production", "ghost-client"));
        Assert.True(receipt!.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.ClientUnknown, receipt.BatchRejectReason);
    }

    [Fact]
    public async Task Omitted_attribution_resolves_to_the_default_tenant()
    {
        await using var app = NewApp();
        var receipt = await PostAsync(app, MakeBatch(appId: null, environment: null, clientId: null, eventId: "evt-d"));
        Assert.False(receipt!.BatchRejected);
        Assert.Equal(1, receipt.AcceptedCount);

        var store = app.Services.GetRequiredService<RSCIAnalyticsStore>();
        var page = await store.SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0,
            AppId: "default", Environment: "production", ClientId: "default"), default);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Header_overrides_body_attribution()
    {
        await using var app = NewApp();
        // Body says default; headers force app-a / staging / pharmacy-42.
        var receipt = await PostAsync(app, MakeBatch(appId: null, environment: null, clientId: null, eventId: "evt-h"),
            req =>
            {
                req.Headers.Add("X-Analytics-App", "app-a");
                req.Headers.Add("X-Analytics-Environment", "staging");
                req.Headers.Add("X-Analytics-Client", "pharmacy-42");
            });
        Assert.False(receipt!.BatchRejected);

        var store = app.Services.GetRequiredService<RSCIAnalyticsStoreFactory>().Get("pharmacy-42", "app-a");
        var page = await store.SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0,
            AppId: "app-a", Environment: "staging", ClientId: "pharmacy-42"), default);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Events_are_isolated_per_app()
    {
        await using var app = NewApp();
        await PostAsync(app, MakeBatch("app-a", "production", "pharmacy-42", eventId: "evt-a"));
        await PostAsync(app, MakeBatch("app-b", "production", "pharmacy-42", eventId: "evt-b"));

        // Database-per-app gives PHYSICAL isolation: each app's events live in its own file. Read
        // each app's DB directly and confirm it holds only its own event (and not the other's).
        var factory = app.Services.GetRequiredService<RSCIAnalyticsStoreFactory>();

        var aOnly = await factory.Get("pharmacy-42", "app-a").SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0), default);
        Assert.Equal(1, aOnly.Total);
        Assert.All(aOnly.Rows, r => Assert.Equal("app-a", r.AppId));

        var bOnly = await factory.Get("pharmacy-42", "app-b").SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0), default);
        Assert.Equal(1, bOnly.Total);
        Assert.All(bOnly.Rows, r => Assert.Equal("app-b", r.AppId));
    }

    [Fact]
    public async Task All_apps_view_merges_across_app_databases()
    {
        await using var app = NewApp();
        await PostAsync(app, MakeBatch("app-a", "production", "pharmacy-42", eventId: "evt-a"));
        await PostAsync(app, MakeBatch("app-b", "production", "pharmacy-42", eventId: "evt-b"));

        // The DI-resolved store is the fan-out facade: an unscoped read merges across every app DB.
        var fanout = app.Services.GetRequiredService<RSCIAnalyticsStore>();
        var all = await fanout.SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0), default);
        Assert.Equal(2, all.Total);

        // …while a scoped read on the same facade delegates to just that app's DB.
        var scoped = await fanout.SearchEventsAsync(new RSCAnalyticsEventFilter(
            null, null, null, null, null, null, null, 100, 0, AppId: "app-a", ClientId: "pharmacy-42"), default);
        Assert.Equal(1, scoped.Total);
        Assert.All(scoped.Rows, r => Assert.Equal("app-a", r.AppId));
    }
}
