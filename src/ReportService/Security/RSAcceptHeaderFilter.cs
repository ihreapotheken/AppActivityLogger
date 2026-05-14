using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace ReportService.Security;

/// <summary>
/// Endpoint filter that rejects requests whose <c>Accept</c> header is present but excludes every
/// media type this service is willing to produce (<c>application/json</c>,
/// <c>application/problem+json</c>, <c>application/octet-stream</c>, <c>application/gzip</c>, or
/// wildcards). A missing <c>Accept</c> header is treated as "anything goes" per RFC 9110.
/// </summary>
public sealed class RSAcceptHeaderFilter : IEndpointFilter
{
    private static readonly string[] Producible =
    {
        "application/json",
        "application/problem+json",
        "application/octet-stream",
        "application/gzip"
    };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var header = ctx.HttpContext.Request.Headers[HeaderNames.Accept];
        if (header.Count == 0) return await next(ctx).ConfigureAwait(false);

        foreach (var raw in header)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (var segment in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var media = segment.Split(';', 2, StringSplitOptions.TrimEntries)[0];
                if (media is "*/*" or "application/*") return await next(ctx).ConfigureAwait(false);
                foreach (var p in Producible)
                {
                    if (string.Equals(media, p, StringComparison.OrdinalIgnoreCase))
                        return await next(ctx).ConfigureAwait(false);
                }
            }
        }

        return Results.StatusCode(StatusCodes.Status406NotAcceptable);
    }
}
