using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReportService.Admin;
using ReportService.Admin.Options;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers CODE-REVIEW finding #33 (MEDIUM): the admin NDJSON export endpoints
/// (/admin/api/analytics/events.ndjson and sessions.ndjson) had no coverage of auth, the row/limit
/// clamp, the newline-delimited shape, or the PII column set. These are cookie-gated admin routes
/// (the global Razor fallback policy), so we use the AdminUiTests login pattern: anonymous must NOT
/// reach the data; an authenticated cookie does. Real admin host + real SQLite store, no mocks.
/// </summary>
public class AdminAnalyticsExportTests
{
    private const string AdminKey = "admin-export-0123456789";
    private const string EventsUrl = "/admin/api/analytics/events.ndjson";
    private const string SessionsUrl = "/admin/api/analytics/sessions.ndjson";

    [Fact]
    public async Task Anonymous_events_export_does_not_return_200()
    {
        await using var app = new ExportFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync(EventsUrl);
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        // The cookie fallback policy bounces an anonymous caller to /Login.
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public async Task Anonymous_sessions_export_does_not_return_200()
    {
        await using var app = new ExportFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync(SessionsUrl);
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public async Task Authenticated_events_export_is_newline_delimited_json_and_carries_only_the_hash()
    {
        await using var app = new ExportFactory();
        await SeedEventsAsync(app, count: 3);
        var client = await AuthedClientAsync(app);

        var res = await client.GetAsync(EventsUrl + "?platform=android");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("application/x-ndjson", res.Content.Headers.ContentType?.ToString());

        var body = await res.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 3, $"expected >= 3 rows, got {lines.Length}");

        foreach (var line in lines)
        {
            // Each non-empty line is a standalone JSON object (NDJSON), not a JSON array element.
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            // PII contract: the hashed identifier is present; no raw email/userId column is leaked.
            Assert.True(doc.RootElement.TryGetProperty("anonymousIdHash", out _));
            Assert.False(doc.RootElement.TryGetProperty("email", out _));
            Assert.False(doc.RootElement.TryGetProperty("userId", out _));
            Assert.False(doc.RootElement.TryGetProperty("anonymousId", out _));
        }
    }

    [Fact]
    public async Task Events_export_respects_the_limit_clamp()
    {
        await using var app = new ExportFactory();
        await SeedEventsAsync(app, count: 12);
        var client = await AuthedClientAsync(app);

        // limit=5 must cap the line count; an absurd limit must not exceed the hard MaxExportRows.
        var capped = await client.GetStringAsync(EventsUrl + "?platform=android&limit=5");
        var lines = capped.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= 5, $"limit=5 should cap rows, got {lines.Length}");

        var huge = await client.GetStringAsync(EventsUrl + "?platform=android&limit=10000000");
        var hugeLines = huge.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(hugeLines.Length <= 50_000, "must not exceed MaxExportRows");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task SeedEventsAsync(ExportFactory app, int count)
    {
        var store = app.Services.GetRequiredService<RSCIAnalyticsStore>();
        var validator = app.Services.GetRequiredService<RSCAnalyticsValidator>();
        var hasher = app.Services.GetRequiredService<RSCAnalyticsIdentifierHasher>();

        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, count).Select(i => new RSCAnalyticsEvent(
            EventId: $"export-evt-{i:D3}", SessionId: "export-session", Sequence: i,
            OccurredAt: now.ToString("O"), Type: "screen", Name: "home", Screen: "home",
            Feature: null, DurationMs: 100, Properties: new Dictionary<string, string>(), Items: null)).ToArray();

        var batch = new RSCAnalyticsBatch(SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(),
            Platform: "android", SdkVersion: "1.0.0", HostAppVersion: "5.6.7", AnonymousId: "anon-export",
            ClientId: null, GeneratedAt: now.ToString("O"), Events: events);

        var verdict = validator.Validate(batch, now);
        await store.WriteBatchAsync(batch, hasher.Hash("anon-export"), null, verdict, now, default);
    }

    private static async Task<HttpClient> AuthedClientAsync(ExportFactory app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginPage = await client.GetStringAsync("/Login");
        var token = ExtractAntiforgery(loginPage);
        using var post = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Key", AdminKey),
            new KeyValuePair<string, string>("__RequestVerificationToken", token!)
        });
        var loginRes = await client.PostAsync("/Login", post);
        Assert.Equal(HttpStatusCode.Redirect, loginRes.StatusCode);
        return client;
    }

    private static string? ExtractAntiforgery(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\"";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        const string valueMarker = "value=\"";
        var start = html.IndexOf(valueMarker, idx, StringComparison.Ordinal) + valueMarker.Length;
        var end = html.IndexOf('"', start);
        return html.Substring(start, end - start);
    }

    private sealed class ExportFactory : WebApplicationFactory<AdminProgram>
    {
        public string ReportsRoot { get; } = Path.Combine(Path.GetTempPath(), $"rs-export-{Guid.NewGuid():N}");
        public string AbuseDbPath { get; } = Path.Combine(Path.GetTempPath(), $"rs-export-abuse-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RSCReportServiceOptions>();
                services.AddSingleton(new RSCReportServiceOptions
                {
                    ApiKey = "unused-in-admin-tests-but-must-be-present-for-startup",
                    ReportsRoot = ReportsRoot,
                    AuthAbuseDbPath = AbuseDbPath,
                    Storage = "FileSystem"
                });

                services.RemoveAll<RSCAnalyticsOptions>();
                services.AddSingleton(new RSCAnalyticsOptions
                {
                    SqliteDbPath = $"analytics-{Guid.NewGuid():N}.db",
                    IdentifierHashPepper = "test-pepper"
                });

                services.RemoveAll<RSAAdminOptions>();
                services.AddSingleton(new RSAAdminOptions { AdminKey = AdminKey, SessionMinutes = 60 });
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
}
