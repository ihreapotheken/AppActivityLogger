using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ReportService.Observability;

/// <summary>
/// Ensures every request carries a correlation id that flows through logs and back to the client.
/// On arrival we accept <c>X-Correlation-ID</c> (or <c>X-Request-ID</c>) from the client if it looks
/// plausible; otherwise we reuse the framework's <see cref="HttpContext.TraceIdentifier"/>. The id is
/// echoed in the response and pushed into the logger scope so every log entry produced while
/// handling the request carries a <c>CorrelationId</c> property.
/// </summary>
public static class RSCCorrelationIdMiddleware
{
    private const string PrimaryHeader = "X-Correlation-ID";
    private const string AliasHeader = "X-Request-ID";
    private const int MaxLength = 128;

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        var loggerFactory = (ILoggerFactory)app.ApplicationServices.GetService(typeof(ILoggerFactory))!;
        var logger = loggerFactory.CreateLogger("ReportService.Observability.CorrelationId");

        return app.Use(async (context, next) =>
        {
            var id = ReadFromClient(context) ?? context.TraceIdentifier;
            context.TraceIdentifier = id;

            // Emit the correlation id on the response before anything else writes headers.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[PrimaryHeader] = id;
                return Task.CompletedTask;
            });

            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = id,
                ["RequestPath"] = context.Request.Path.Value ?? string.Empty,
                ["RequestMethod"] = context.Request.Method
            }))
            {
                await next().ConfigureAwait(false);
            }
        });
    }

    private static string? ReadFromClient(HttpContext context)
    {
        if (TryRead(context, PrimaryHeader, out var id)) return id;
        if (TryRead(context, AliasHeader, out id)) return id;
        return null;
    }

    private static bool TryRead(HttpContext context, string name, out string? id)
    {
        id = null;
        if (!context.Request.Headers.TryGetValue(name, out var raw) || raw.Count == 0) return false;
        var value = raw.ToString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength) return false;
        foreach (var ch in value)
        {
            // ASCII printable (0x20–0x7E) only; no control bytes, no header-injection risk.
            if (ch < 0x20 || ch > 0x7E) return false;
        }
        id = value;
        return true;
    }
}
