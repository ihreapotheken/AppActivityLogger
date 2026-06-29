using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReportService.DeepLinks;
using ReportService.Endpoints;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// End-to-end coverage of the deferred deep-linking routes: auth, click capture + link resolution,
/// IP matching within the window, the claim-once guarantee, and the no-match path.
/// </summary>
public class DeepLinkEndpointTests
{
    private const string ClicksUrl = "/api/v2/deeplinks/clicks";
    private const string MatchUrl = "/api/v2/deeplinks/match";

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task SeedLinkAsync(IngestionAppFactory app, string slug, string pattern, string redirect, bool enabled = true)
    {
        var store = app.Services.GetRequiredService<RSCIDeferredDeepLinkStore>();
        await store.UpsertLinkAsync(slug, slug, pattern, redirect, enabled, CancellationToken.None);
    }

    [Fact]
    public async Task Unauthenticated_record_gets_401()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        var res = await client.PostAsync(ClicksUrl, Body(new { pageUrl = "https://site/promo/spring", ip = "203.0.113.7" }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Record_without_page_url_returns_400()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var res = await client.PostAsync(ClicksUrl, Body(new { ip = "203.0.113.7" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Record_then_match_by_ip_returns_configured_redirect()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        const string ip = "203.0.113.7";
        var rec = await client.PostAsync(ClicksUrl, Body(new { pageUrl = "https://site.example/promo/spring/landing", ip }));
        Assert.Equal(HttpStatusCode.Created, rec.StatusCode);
        var recBody = await rec.Content.ReadFromJsonAsync<RSDeepLinkClickResponse>();
        Assert.True(recBody!.Recorded);
        Assert.True(recBody.Matched);
        Assert.Equal("myapp://promo/spring", recBody.RedirectUrl);

        var match = await client.GetAsync($"{MatchUrl}?ip={ip}");
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        var matchBody = await match.Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.True(matchBody!.Matched);
        Assert.Equal("spring-promo", matchBody.Slug);
        Assert.Equal("myapp://promo/spring", matchBody.RedirectUrl);
        Assert.Equal("https://site.example/promo/spring/landing", matchBody.PageUrl);
    }

    [Fact]
    public async Task Match_for_unknown_ip_returns_matched_false()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        await client.PostAsync(ClicksUrl, Body(new { pageUrl = "https://site/promo/spring", ip = "203.0.113.7" }));

        var match = await client.GetAsync($"{MatchUrl}?ip=198.51.100.42");
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        var matchBody = await match.Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.False(matchBody!.Matched);
        Assert.Null(matchBody.RedirectUrl);
    }

    [Fact]
    public async Task Click_with_no_matching_link_does_not_match()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        const string ip = "203.0.113.99";
        var rec = await client.PostAsync(ClicksUrl, Body(new { pageUrl = "https://site/unrelated/page", ip }));
        var recBody = await rec.Content.ReadFromJsonAsync<RSDeepLinkClickResponse>();
        Assert.True(recBody!.Recorded);
        Assert.False(recBody.Matched);

        var match = await client.GetAsync($"{MatchUrl}?ip={ip}");
        var matchBody = await match.Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.False(matchBody!.Matched);
    }

    [Fact]
    public async Task Matched_click_is_claimed_and_not_returned_twice()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        const string ip = "203.0.113.7";
        await client.PostAsync(ClicksUrl, Body(new { pageUrl = "https://site/promo/spring", ip }));

        var first = await (await client.GetAsync($"{MatchUrl}?ip={ip}")).Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.True(first!.Matched);

        // Default claim=true consumes the click, so a second match for the same IP finds nothing.
        var second = await (await client.GetAsync($"{MatchUrl}?ip={ip}")).Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.False(second!.Matched);
    }

    [Fact]
    public async Task Disabled_link_does_not_match()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        const string ip = "203.0.113.7";
        await client.PostAsync(ClicksUrl, Body(new { pageUrl = "https://site/promo/spring", ip }));

        // Disable the link after the click was captured: the pending click must stop matching.
        var store = app.Services.GetRequiredService<RSCIDeferredDeepLinkStore>();
        await store.SetLinkEnabledAsync("spring-promo", false, CancellationToken.None);

        var match = await (await client.GetAsync($"{MatchUrl}?ip={ip}")).Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.False(match!.Matched);
    }
}
