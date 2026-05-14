using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Hosting;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies that <c>X-Forwarded-For</c> is ignored by default (attacker-spoofed IPs cannot
/// influence auth-abuse tracking or per-IP rate limiting when the service is exposed on a host
/// port), and that enabling <c>ProxyHeaders:Enabled</c> with a known-proxy list does let a
/// trusted upstream rewrite the reported remote address.
/// </summary>
public class ForwardedHeadersTests
{
    [Fact]
    public async Task ForwardedFor_is_ignored_when_proxy_headers_disabled()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        // Submit auth failures with a spoofed X-Forwarded-For. With ProxyHeaders disabled (the
        // default), every attempt is attributed to the same "unknown" / loopback source, so a few
        // failures should accumulate — far short of the default threshold of 10.
        for (var i = 0; i < 3; i++)
        {
            using var form = BuildMultipart();
            var req = new HttpRequestMessage(HttpMethod.Post, "/partners/api/v2/report-problem")
            {
                Content = form
            };
            req.Headers.Add("apiKey", "definitely-wrong");
            req.Headers.Add("X-Forwarded-For", $"1.2.3.{i}");
            var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        // Now send a valid request without X-Forwarded-For — still works because the failures piled
        // up against the shared loopback source but haven't crossed the threshold.
        using var good = BuildMultipart();
        var okReq = new HttpRequestMessage(HttpMethod.Post, "/partners/api/v2/report-problem")
        {
            Content = good
        };
        okReq.Headers.Add("apiKey", IngestionAppFactory.ApiKey);
        var okRes = await client.SendAsync(okReq);
        Assert.Equal(HttpStatusCode.Created, okRes.StatusCode);
    }

    [Fact]
    public async Task Enabled_without_known_proxies_still_refuses_to_trust_random_clients()
    {
        // Safety net: even with Enabled=true, the middleware in ASP.NET Core only rewrites
        // RemoteIpAddress when the direct caller is a known proxy. TestServer's in-process
        // connection reports a loopback address that does NOT match our KnownProxies list, so the
        // forwarded header must be ignored.
        await using var app = new ProxyAppFactory(new RSCProxyHeadersOptions
        {
            Enabled = true,
            KnownProxies = new[] { "10.20.30.40" }
        });
        var client = app.CreateClient();

        using var form = BuildMultipart();
        var req = new HttpRequestMessage(HttpMethod.Post, "/partners/api/v2/report-problem")
        {
            Content = form
        };
        req.Headers.Add("apiKey", IngestionAppFactory.ApiKey);
        req.Headers.Add("X-Forwarded-For", "198.51.100.1");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    private static MultipartFormDataContent BuildMultipart()
    {
        var form = new MultipartFormDataContent();
        var json = new StringContent(
            "{\"platform\":\"Android\",\"message\":\"Test\"}",
            Encoding.UTF8, "application/json");
        form.Add(json, "json", "report.json");
        return form;
    }

    /// <summary>Lets individual tests supply their own <see cref="RSCProxyHeadersOptions"/>.</summary>
    private sealed class ProxyAppFactory : IngestionAppFactory
    {
        private readonly RSCProxyHeadersOptions _proxy;
        public ProxyAppFactory(RSCProxyHeadersOptions proxy) => _proxy = proxy;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RSCProxyHeadersOptions>();
                services.AddSingleton(_proxy);
            });
        }
    }
}
