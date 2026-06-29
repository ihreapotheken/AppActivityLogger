using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Options;
using ReportService.Storage.ApiKeys;

namespace ReportService.Security;

/// <summary>
/// Builds the global rate-limit partition for a request. Partitions by the resolved API key when one
/// is present (so each key — and the static root key — gets its own budget), otherwise by source IP.
/// Uses a sliding window (vs. the old fixed window) to remove the ~2× burst at window boundaries.
///
/// Runs in the rate-limiter middleware, which executes BEFORE authentication, so it resolves the key
/// itself via the cache-backed <see cref="RSCIApiKeyStore"/> (no DB hit, no dependency on the auth
/// pipeline). The partition factory runs once per distinct partition; the per-request cost is one
/// header read + SHA-256 + dictionary lookup.
/// </summary>
public static class RSCApiKeyRateLimiter
{
    private const int WindowSegments = 6; // 10s segments over a 1-minute window
    private const int UnknownSourceCap = 30; // ceiling for the shared null-IP bucket

    public static RateLimitPartition<string> Partition(HttpContext ctx)
    {
        var options = ctx.RequestServices.GetRequiredService<RSCReportServiceOptions>();

        var presented = TryReadKey(ctx);
        if (presented is not null)
        {
            var store = ctx.RequestServices.GetRequiredService<RSCIApiKeyStore>();
            var resolution = RSCApiKeyResolver.Resolve(presented, options, store);
            if (resolution is not null)
                return Window($"key:{resolution.KeyId}", resolution.EffectiveRateLimitPerMinute);
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrEmpty(ip))
        {
            // Null source (misconfigured proxy, unix socket): one small shared bucket rather than an
            // unbounded "unknown" partition that every such caller could pile into.
            return Window("ip:unknown", Math.Min(options.RateLimitPermitsPerMinute, UnknownSourceCap));
        }

        return Window($"ip:{ip}", options.RateLimitPermitsPerMinute);
    }

    private static RateLimitPartition<string> Window(string key, int permitsPerMinute) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = WindowSegments,
            QueueLimit = 0,
            AutoReplenishment = true
        });

    private static string? TryReadKey(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue(RSApiKeyAuthenticationOptions.HeaderName, out var values) && values.Count == 1
            ? values.ToString()
            : null;
}
