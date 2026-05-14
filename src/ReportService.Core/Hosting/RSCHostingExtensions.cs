using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReportService.Hosting;

/// <summary>
/// Shared hosting helpers used by both the ingestion service and the admin UI: Kestrel hardening,
/// forwarded-headers wiring, a consistent security-header response middleware, and a small helper
/// that inspects configuration to decide whether HTTPS redirection / HSTS should run in this process.
/// </summary>
public static class RSCHostingExtensions
{
    /// <summary>
    /// Applies the shared Kestrel hardening: disables the <c>Server:</c> header, caps header /
    /// keepalive timeouts, bounds concurrent connections, and installs slowloris-defeating minimum
    /// data rates on both request and response.
    /// </summary>
    public static void ConfigureHardenedKestrel(KestrelServerOptions k, long? maxRequestBodySize = null)
    {
        if (maxRequestBodySize.HasValue) k.Limits.MaxRequestBodySize = maxRequestBodySize.Value;
        k.AddServerHeader = false;
        k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
        k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        k.Limits.MaxConcurrentConnections = 1024;
        k.Limits.MaxConcurrentUpgradedConnections = 64;
        k.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(10));
        k.Limits.MinResponseDataRate = new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Applies forwarded-header processing only when <see cref="RSCProxyHeadersOptions.Enabled"/> is
    /// true. Disabled by default — trusting <c>X-Forwarded-*</c> from an arbitrary client is a
    /// source-IP-spoofing hole when the service is reachable on a host-bound port.
    /// </summary>
    public static IApplicationBuilder UseStandardForwardedHeaders(this IApplicationBuilder app, RSCProxyHeadersOptions options)
    {
        if (!options.Enabled) return app;

        var fwd = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = Math.Max(1, options.ForwardLimit)
        };
        fwd.KnownProxies.Clear();
        fwd.KnownNetworks.Clear();

        foreach (var ip in options.KnownProxies)
            if (IPAddress.TryParse(ip, out var parsed)) fwd.KnownProxies.Add(parsed);

        foreach (var net in options.KnownNetworks)
        {
            var parts = net.Split('/', 2);
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var baseIp) && int.TryParse(parts[1], out var prefix))
                fwd.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(baseIp, prefix));
        }

        if (fwd.KnownProxies.Count == 0 && fwd.KnownNetworks.Count == 0)
        {
            var logger = app.ApplicationServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            logger?.CreateLogger("ReportService.Hosting.ProxyHeaders").LogWarning(
                "ProxyHeaders:Enabled=true but KnownProxies + KnownNetworks are empty — the service will trust X-Forwarded-* from any upstream hop. Configure at least one entry unless the deployment topology already fences this.");
        }

        return app.UseForwardedHeaders(fwd);
    }

    /// <summary>
    /// Adds the baseline response headers that every endpoint emits: <c>X-Content-Type-Options</c>,
    /// <c>X-Frame-Options</c>, <c>Referrer-Policy</c>, <c>Cache-Control</c>. Individual apps can
    /// layer additional headers (e.g. a stricter CSP for the admin UI) via a subsequent middleware.
    /// </summary>
    public static IApplicationBuilder UseStandardSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cache-Control"] = "no-store";
            await next().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Returns true when configuration names at least one HTTPS endpoint. Checks
    /// <c>ASPNETCORE_URLS</c> / <c>urls</c> first (either replaces <c>Kestrel:Endpoints</c> when
    /// set); falls back to configured Kestrel endpoints when neither env var is present.
    /// </summary>
    public static bool HasHttpsEndpoint(IConfiguration configuration)
    {
        var urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"];
        if (!string.IsNullOrEmpty(urls))
        {
            foreach (var u in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrEmpty(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
