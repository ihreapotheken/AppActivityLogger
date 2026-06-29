using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies the per-key rate limiter: each key gets its own budget (the per-key override), and
/// exhausting one key does not affect another. Driven through the real ingestion host.
/// </summary>
public class ApiKeyRateLimitTests
{
    private static HttpRequestMessage Get(string url, string key)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Add("apiKey", key);
        return r;
    }

    private static async Task<string> MintAdminAsync(HttpClient client, int rate)
    {
        var body = JsonContent.Create(new { role = "admin", rateLimitPerMinute = rate });
        var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/keys")
        {
            Headers = { { "apiKey", IngestionAppFactory.ApiKey } },
            Content = body
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("key").GetString()!;
    }

    [Fact]
    public async Task Per_key_override_caps_that_key_and_returns_429_with_retry_after()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        var key = await MintAdminAsync(client, rate: 2); // budget of 2/min for this key

        // GET /api/v1/keys is admin-authorized; the limiter (running before auth) caps it at 2.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/keys", key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/keys", key))).StatusCode);

        var third = await client.SendAsync(Get("/api/v1/keys", key));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), third.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Two_keys_have_independent_budgets()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        var keyA = await MintAdminAsync(client, rate: 2);
        var keyB = await MintAdminAsync(client, rate: 2);

        // Exhaust key A.
        await client.SendAsync(Get("/api/v1/keys", keyA));
        await client.SendAsync(Get("/api/v1/keys", keyA));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/api/v1/keys", keyA))).StatusCode);

        // Key B is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/keys", keyB))).StatusCode);
    }
}
