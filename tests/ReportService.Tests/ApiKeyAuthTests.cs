using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// End-to-end coverage for managed API-key auth + the key-management REST surface, driven through
/// the real ingestion host (<see cref="IngestionAppFactory"/>). The factory's <c>ApiKey</c> is the
/// static root-admin used to bootstrap managed keys.
/// </summary>
public class ApiKeyAuthTests
{
    private const string ValidReport =
        "{\"platform\":\"Android\",\"message\":\"apikey-test\",\"pharmacyId\":\"DE-1\"}";

    private static HttpRequestMessage Req(HttpMethod method, string url, string? key, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (key is not null) r.Headers.Add("apiKey", key);
        if (body is not null) r.Content = body;
        return r;
    }

    private static StringContent ReportBody() => new(ValidReport, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> MintAsync(HttpClient client, string adminKey, string role, int? expiresInDays = null, int? rate = null, string? clientId = null)
    {
        var body = JsonContent.Create(new { role, expiresInDays, rateLimitPerMinute = rate, clientId });
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/keys", adminKey, body));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Root_key_mints_a_client_key_that_can_ingest()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        // A client key is bound to a catalog client; "default" is self-seeded, so it ingests exactly
        // as the old unbound key did (which also resolved to the default client).
        var created = await MintAsync(client, IngestionAppFactory.ApiKey, "client", clientId: "default");
        var clientKey = created.GetProperty("key").GetString()!;
        Assert.Equal("client", created.GetProperty("role").GetString());
        Assert.StartsWith("rsk_", clientKey, StringComparison.Ordinal);

        var ingest = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/reports", clientKey, ReportBody()));
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
    }

    [Fact]
    public async Task Management_requires_admin_role()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        // No key → 401.
        var anon = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/keys", key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Admin key (minted by the root key) → 200.
        var adminKey = (await MintAsync(client, IngestionAppFactory.ApiKey, "admin")).GetProperty("key").GetString()!;
        var asAdmin = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/keys", adminKey));
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);

        // Client key authenticates but is forbidden from management → 403.
        var clientKey = (await MintAsync(client, IngestionAppFactory.ApiKey, "client", clientId: "default")).GetProperty("key").GetString()!;
        var asClient = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/keys", clientKey));
        Assert.Equal(HttpStatusCode.Forbidden, asClient.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        var created = await MintAsync(client, IngestionAppFactory.ApiKey, "client", clientId: "default");
        var clientKey = created.GetProperty("key").GetString()!;
        var id = created.GetProperty("id").GetString()!;

        // Works before revocation.
        var before = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/reports", clientKey, ReportBody()));
        Assert.Equal(HttpStatusCode.Created, before.StatusCode);

        var del = await client.SendAsync(Req(HttpMethod.Delete, $"/api/v1/keys/{id}", IngestionAppFactory.ApiKey));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var after = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/reports", clientKey, ReportBody()));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Unknown_key_is_unauthorized()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/reports", "rsk_bogus_key", ReportBody()));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
