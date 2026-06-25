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
/// End-to-end coverage of POST /api/v2/analytics/events: auth, content-type, schema rejection,
/// happy path, and idempotent replay.
/// </summary>
public class AnalyticsEndpointTests
{
    private const string Url = "/api/v2/analytics/events";

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object MakeBatch(int schemaVersion = 1, string? batchId = null, params object[] events) => new
    {
        schemaVersion,
        batchId = batchId ?? Guid.NewGuid().ToString(),
        platform = "android",
        sdkVersion = "1.0.0",
        hostAppVersion = "5.6.7",
        anonymousId = "anon-1",
        clientId = (string?)null,
        generatedAt = DateTimeOffset.UtcNow.ToString("O"),
        events,
    };

    private static object MakeEvent(string? eventId = null) => new
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
    };

    [Fact]
    public async Task Unauthenticated_request_gets_401()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        var res = await client.PostAsync(Url, Body(MakeBatch(events: MakeEvent())));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_content_type_returns_415()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        var res = await client.PostAsync(Url,
            new StringContent("not-json", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
    }

    [Fact]
    public async Task Happy_path_returns_202_with_receipt()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var batchId = Guid.NewGuid().ToString();
        var res = await client.PostAsync(Url, Body(MakeBatch(batchId: batchId, events: MakeEvent())));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(batchId, receipt!.BatchId);
        Assert.Equal(1, receipt.AcceptedCount);
        Assert.Equal(0, receipt.RejectedCount);
        Assert.False(receipt.BatchRejected);
    }

    [Fact]
    public async Task Unsupported_schema_version_dlqs_with_202_and_batch_rejected_receipt()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var res = await client.PostAsync(Url, Body(MakeBatch(schemaVersion: 99, events: MakeEvent())));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var receipt = await res.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.NotNull(receipt);
        Assert.True(receipt!.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.SchemaVersionUnsupported, receipt.BatchRejectReason);
    }

    [Fact]
    public async Task Replay_with_same_batch_and_event_ids_is_deduped()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var batchId = Guid.NewGuid().ToString();
        var ev = MakeEvent(eventId: "evt-fixed");
        var payload = Body(MakeBatch(batchId: batchId, events: ev));

        var first = await client.PostAsync(Url, payload);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // Resend a fresh request with the same body. UNIQUE(platform, event_id) should reject the
        // event as a duplicate; the receipt reports it.
        var second = await client.PostAsync(Url, Body(MakeBatch(batchId: batchId, events: ev)));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var receipt = await second.Content.ReadFromJsonAsync<RSCAnalyticsBatchReceipt>();
        Assert.Equal(0, receipt!.AcceptedCount);
        Assert.Equal(1, receipt.DuplicateCount);
    }

    [Fact]
    public async Task When_analytics_disabled_endpoint_returns_503()
    {
        await using var app = new IngestionAppFactory();
        app.ConfigureAnalytics = o => o with { Enabled = false };
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var res = await client.PostAsync(Url, Body(MakeBatch(events: MakeEvent())));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }
}
