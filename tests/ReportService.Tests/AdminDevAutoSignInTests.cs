using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReportService.Admin;
using ReportService.Admin.Options;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Locks the DevAutoSignIn trust boundary. The bypass must engage for a DIRECT operator request
/// (a loopback <em>Host</em> — `localhost`/`127.0.0.1`/`[::1]` — which survives the Docker host-port
/// forward even though the source IP arrives non-loopback), but must NOT engage for a request that
/// traversed the cloudflared tunnel / a reverse proxy. That covers two failure modes: a proxy that
/// forwards Cf-Connecting-Ip / X-Forwarded-For (Cloudflare's edge adds these), AND — the gap that
/// previously exposed the admin UI — a tunnel that strips every forwarding header but still carries
/// a non-loopback Host. The gate is positive (require loopback Host) so it fails closed.
/// </summary>
public class AdminDevAutoSignInTests
{
    private sealed class DevSignInFactory : WebApplicationFactory<AdminProgram>
    {
        public string ReportsRoot { get; } = Path.Combine(Path.GetTempPath(), $"rs-devsignin-{Guid.NewGuid():N}");
        public string AbuseDbPath { get; } = Path.Combine(Path.GetTempPath(), $"rs-devsignin-abuse-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            // The DevAutoSignIn middleware reads the startup-bound config value, so flip it via
            // configuration (UseSetting), not just the DI options singleton.
            builder.UseSetting("Admin:DevAutoSignIn", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RSCReportServiceOptions>();
                services.AddSingleton(new RSCReportServiceOptions
                {
                    ApiKey = "unused-in-admin-tests-but-present-for-startup",
                    ReportsRoot = ReportsRoot,
                    AuthAbuseDbPath = AbuseDbPath,
                    Storage = "FileSystem"
                });
                services.RemoveAll<RSAAdminOptions>();
                services.AddSingleton(new RSAAdminOptions { AdminKey = "admin-test-0123456789", SessionMinutes = 60, DevAutoSignIn = true });
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

    [Fact]
    public async Task Direct_request_is_auto_signed_in()
    {
        await using var app = new DevSignInFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // No forwarding headers — the operator's own machine (loopback, or a Docker host-port hop).
        var res = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("Cf-Connecting-Ip", "203.0.113.7")]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    public async Task Request_arriving_via_proxy_or_tunnel_still_requires_login(string header, string value)
    {
        await using var app = new DevSignInFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(header, value);

        // A request that traversed the tunnel/proxy must NOT be auto-signed-in.
        var res = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Login", res.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("random123.trycloudflare.com")] // quick-tunnel public hostname
    [InlineData("reports.example.com")]          // named-tunnel public hostname
    [InlineData("report-service:8080")]          // cloudflared rewriting Host to the compose origin
    public async Task Request_with_non_loopback_host_and_no_forwarding_headers_requires_login(string host)
    {
        // The exact gap that exposed the admin UI: a tunnel can strip every Cf-* / X-Forwarded-*
        // header, so the old "engage unless a proxy header is present" check failed open. The Host
        // is never loopback for such callers, so the positive loopback-Host gate must keep them out.
        await using var app = new DevSignInFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Host = host;
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Login", res.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("localhost:8082")]
    [InlineData("127.0.0.1:8082")]
    [InlineData("[::1]:8082")]
    public async Task Operator_on_loopback_host_is_auto_signed_in(string host)
    {
        // Whatever HOST_PORT the operator binds, the Host header's host part is loopback and the
        // bypass must still engage so the dockerised dev UX keeps opening without the admin key.
        await using var app = new DevSignInFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Host = host;
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
