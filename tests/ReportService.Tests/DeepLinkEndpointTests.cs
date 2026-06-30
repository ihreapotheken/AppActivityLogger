using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ReportService.DeepLinks;
using ReportService.Endpoints;
using ReportService.Options;
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
    private const string RetentionUrl = "/api/v2/deeplinks/click-retention";

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
    public async Task Smart_link_is_anonymous_records_ip_and_redirects()
    {
        await using var app = new IngestionAppFactory();
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        // SendAsync lets us set a real RemoteIpAddress (the TestServer leaves it null otherwise).
        // No apiKey is set on the request — the hosted smart link is anonymous.
        var ctx = await app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("localhost");
            c.Request.Path = "/dl/spring-promo";
            c.Request.Headers.Referer = "https://shop.example/promo/spring";
            c.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        });

        Assert.Equal(StatusCodes.Status302Found, ctx.Response.StatusCode);
        Assert.Equal("myapp://promo/spring", ctx.Response.Headers.Location.ToString());

        // The visit was recorded against the caller's IP and the referring page, so the app's
        // later match for that IP resolves to the configured redirect.
        var store = app.Services.GetRequiredService<RSCIDeferredDeepLinkStore>();
        var clicks = await store.ListRecentClicksAsync(10, CancellationToken.None);
        Assert.Contains(clicks, c => c.LinkSlug == "spring-promo"
            && c.Ip == "203.0.113.7"
            && c.PageUrl == "https://shop.example/promo/spring");

        var match = await store.FindMatchForIpAsync(
            "203.0.113.7", TimeSpan.FromHours(24), claim: true, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal("myapp://promo/spring", match!.RedirectUrl);
    }

    [Fact]
    public async Task Smart_link_forwards_query_params_and_match_returns_them()
    {
        await using var app = new IngestionAppFactory();
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        const string ip = "203.0.113.7";
        var ctx = await app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("localhost");
            c.Request.Path = "/dl/spring-promo";
            c.Request.QueryString = new QueryString("?utm_source=newsletter&promo=ABC");
            c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        });

        // The 302 carries the captured params appended to the base redirect.
        Assert.Equal(StatusCodes.Status302Found, ctx.Response.StatusCode);
        var location = ctx.Response.Headers.Location.ToString();
        Assert.StartsWith("myapp://promo/spring?", location);
        Assert.Contains("utm_source=newsletter", location);
        Assert.Contains("promo=ABC", location);

        // The app's match returns the params object AND a redirect with them appended.
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        var match = await (await client.GetAsync($"{MatchUrl}?ip={ip}")).Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.True(match!.Matched);
        Assert.NotNull(match.Params);
        Assert.Equal("newsletter", match.Params!["utm_source"]);
        Assert.Equal("ABC", match.Params["promo"]);
        Assert.Contains("utm_source=newsletter", match.RedirectUrl);
    }

    [Fact]
    public async Task Post_clicks_with_params_are_captured_and_returned_on_match()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        const string ip = "203.0.113.50";
        var rec = await client.PostAsync(ClicksUrl, Body(new
        {
            pageUrl = "https://site/promo/spring",
            ip,
            @params = new Dictionary<string, string> { ["utm_source"] = "fb", ["cid"] = "42" }
        }));
        var recBody = await rec.Content.ReadFromJsonAsync<RSDeepLinkClickResponse>();
        Assert.True(recBody!.Matched);
        Assert.Contains("utm_source=fb", recBody.RedirectUrl);

        var match = await (await client.GetAsync($"{MatchUrl}?ip={ip}")).Content.ReadFromJsonAsync<RSDeepLinkMatchResponse>();
        Assert.True(match!.Matched);
        Assert.Equal("fb", match.Params!["utm_source"]);
        Assert.Equal("42", match.Params["cid"]);
    }

    [Fact]
    public async Task Smart_link_caps_captured_params_at_the_configured_limit()
    {
        await using var app = new IngestionAppFactory();
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring");

        // 30 params, default cap is 16 → the stored/returned set is capped.
        var qs = "?" + string.Join("&", Enumerable.Range(0, 30).Select(i => $"p{i}={i}"));
        const string ip = "203.0.113.77";
        await app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("localhost");
            c.Request.Path = "/dl/spring-promo";
            c.Request.QueryString = new QueryString(qs);
            c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        });

        var store = app.Services.GetRequiredService<RSCIDeferredDeepLinkStore>();
        var match = await store.FindMatchForIpAsync(ip, TimeSpan.FromHours(24), claim: false, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(match!.QueryParams);
        Assert.Equal(new RSCDeepLinkOptions().MaxQueryParams, match.QueryParams!.Count);
    }

    [Fact]
    public async Task Smart_link_unknown_slug_returns_404()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var res = await client.GetAsync("/dl/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Smart_link_for_disabled_slug_returns_404()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        await SeedLinkAsync(app, "spring-promo", "/promo/spring", "myapp://promo/spring", enabled: false);

        var res = await client.GetAsync("/dl/spring-promo");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
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
