using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Analytics;
using ReportService.Models;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// End-to-end coverage of POST /api/v2/analytics/server-events — the backend / server-to-server
/// reporting path that coexists with the SDK route. Asserts auth, defaulting/synthesis, idempotency,
/// validation, the disabled switch, and that a reported event actually lands in the store under the
/// expected platform.
/// </summary>
public class ServerAnalyticsEndpointTests
{
    private const string Url = "/api/v2/analytics/server-events";

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static HttpClient Authed(IngestionAppFactory app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        return client;
    }

    // A backend reporting a completed purchase: type=ecommerce, name=purchase, no SDK envelope.
    private static object PurchaseRequest(string orderId, string? platform = null, string? sessionId = null) => new
    {
        platform,
        subjectId = "user-42",
        clientId = (string?)null,
        source = "order-service",
        events = new[]
        {
            new
            {
                name = "purchase",
                type = "ecommerce",
                eventId = $"purchase-{orderId}",
                sessionId,
                occurredAt = DateTimeOffset.UtcNow.ToString("O"),
                feature = "otc",
                properties = new Dictionary<string, string>
                {
                    ["order_id"] = orderId, ["total"] = "19.98", ["currency"] = "EUR"
                },
                items = new[]
                {
                    new { itemId = "pzn-00001", name = "Ibuprofen 400mg", category = "pain_relief", price = 9.99m, quantity = 2, currency = "EUR" }
                }
            }
        }
    };

    [Fact]
    public async Task Unauthenticated_request_gets_401()
    {
        await using var app = new IngestionAppFactory();
        var res = await app.CreateClient().PostAsync(Url, Body(PurchaseRequest("1001")));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_content_type_returns_415()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);
        var res = await client.PostAsync(Url, new StringContent("nope", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
    }

    [Fact]
    public async Task Empty_events_returns_400()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);
        var res = await client.PostAsync(Url, Body(new { subjectId = "u", events = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Backend_purchase_is_accepted_and_stored_as_backend_platform()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(PurchaseRequest("2001")));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(1, receipt!.AcceptedCount);
        Assert.Equal(0, receipt.RejectedCount);
        Assert.False(receipt.BatchRejected);

        // The event landed in the same store the SDK path writes to, attributed to "backend".
        var store = app.Services.GetRequiredService<RSCIAnalyticsStore>();
        var page = await store.SearchEventsAsync(
            new RSCAnalyticsEventFilter(
                Platform: RSCAnalyticsPlatforms.Backend, Type: "ecommerce", Name: "purchase",
                Screen: null, SessionId: null, From: null, Until: null, Limit: 10, Offset: 0),
            CancellationToken.None);
        Assert.Contains(page.Rows, r => r.EventId == "purchase-2001" && r.Platform == RSCAnalyticsPlatforms.Backend);
    }

    [Fact]
    public async Task Minimal_event_synthesizes_required_fields_and_is_accepted()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        // Only a name — everything else (eventId, sessionId, sequence, occurredAt, type) synthesized.
        var res = await client.PostAsync(Url, Body(new { events = new[] { new { name = "feature_flag_evaluated" } } }));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.Equal(1, receipt!.AcceptedCount);
        Assert.False(receipt.BatchRejected);
    }

    [Fact]
    public async Task Replay_with_same_event_id_is_deduped()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var first = await client.PostAsync(Url, Body(PurchaseRequest("3001")));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>())!.AcceptedCount);

        // Same business key → same eventId → deduped on UNIQUE(platform, event_id).
        var second = await client.PostAsync(Url, Body(PurchaseRequest("3001")));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var receipt = await second.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.Equal(0, receipt!.AcceptedCount);
        Assert.Equal(1, receipt.DuplicateCount);
    }

    [Fact]
    public async Task Caller_can_attribute_to_a_device_platform_to_join_a_session()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(PurchaseRequest("4001", platform: "ios", sessionId: "s-ios-abc")));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var store = app.Services.GetRequiredService<RSCIAnalyticsStore>();
        var page = await store.SearchEventsAsync(
            new RSCAnalyticsEventFilter("ios", "ecommerce", "purchase", null, "s-ios-abc", null, null, 10, 0),
            CancellationToken.None);
        Assert.Contains(page.Rows, r => r.EventId == "purchase-4001" && r.Platform == "ios" && r.SessionId == "s-ios-abc");
    }

    [Fact]
    public async Task Unknown_platform_is_batch_rejected_in_the_receipt()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(PurchaseRequest("5001", platform: "desktop")));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.True(receipt!.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PlatformUnknown, receipt.BatchRejectReason);
    }

    [Fact]
    public async Task When_analytics_disabled_endpoint_returns_503()
    {
        await using var app = new IngestionAppFactory();
        app.ConfigureAnalytics = o => o with { Enabled = false };
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(PurchaseRequest("6001")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Event_without_a_name_is_a_hard_400_not_a_silent_dlq()
    {
        // CODE-REVIEW finding #7: name is the one documented-required field. A caller that omits it
        // now gets a clear 400 up front instead of a deceptive 202 with the event dead-lettered as
        // missing_required_field.
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(new
        {
            subjectId = "user-1",
            events = new[] { new { type = "action", eventId = "evt-noname" } }
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task EventId_equal_to_subjectId_is_a_400_to_block_a_pii_leak_into_the_envelope()
    {
        // CODE-REVIEW finding #44: eventId/sessionId are stored verbatim and exported, while
        // subjectId is hashed precisely because it is a raw account key. Routing subjectId into the
        // eventId column would permanently leak a raw identifier — reject outright.
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(new
        {
            subjectId = "user-secret-42",
            events = new[] { new { name = "purchase", eventId = "user-secret-42" } }
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SessionId_equal_to_subjectId_is_a_400_to_block_a_pii_leak_into_the_envelope()
    {
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(new
        {
            subjectId = "user-secret-99",
            events = new[] { new { name = "purchase", eventId = "evt-ok", sessionId = "user-secret-99" } }
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Ecommerce_item_with_blank_itemId_is_rejected_in_the_receipt()
    {
        // CODE-REVIEW finding #7: an items[] entry whose itemId deserializes to null/blank violates
        // the contract. The validator rejects the carrying event (per-event), so the receipt shows
        // a rejection rather than persisting a contract-violating row.
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var res = await client.PostAsync(Url, Body(new
        {
            subjectId = "user-7",
            events = new[]
            {
                new
                {
                    name = "purchase",
                    type = "ecommerce",
                    eventId = "evt-baditem",
                    occurredAt = DateTimeOffset.UtcNow.ToString("O"),
                    items = new[] { new { itemId = "", name = "Mystery", price = 1.0m, quantity = 1 } }
                }
            }
        }));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.Equal(0, receipt!.AcceptedCount);
        Assert.Equal(1, receipt.RejectedCount);
    }

    [Fact]
    public async Task Past_dated_backfill_event_is_dead_lettered_as_clock_skew()
    {
        // CODE-REVIEW finding #34/#49/#51: the symmetric skew window (default 24h) means a backend
        // backfilling a purchase that completed 3 days ago is dead-lettered as ClockSkew while the
        // receipt still returns 202 with rejectedCount>0. This characterizes the now-documented
        // behavior so it is intentional; it turns red the moment a backfill allowance is added.
        await using var app = new IngestionAppFactory();
        var client = Authed(app);

        var threeDaysAgo = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        var res = await client.PostAsync(Url, Body(new
        {
            subjectId = "user-backfill",
            source = "order-service",
            events = new[]
            {
                new { name = "purchase", type = "ecommerce", eventId = "evt-backfill", occurredAt = threeDaysAgo }
            }
        }));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.Equal(0, receipt!.AcceptedCount);
        Assert.True(receipt.RejectedCount > 0);
    }
}
