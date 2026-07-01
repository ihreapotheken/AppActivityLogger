using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReportService.Audit;
using ReportService.Security;
using ReportService.Storage.Catalog;

namespace ReportService.Endpoints;

/// <summary>
/// Client-facing REST surface for a client to self-manage <b>its own apps</b> over JSON, using its
/// API access key. The client is derived from the authenticated key's
/// <see cref="RSCTenantClaims.ClientId"/> binding — a key that isn't bound to a client (the static
/// root key or a plain operator key) gets <c>403</c>, since it has no client whose apps to manage.
/// Apps are the analytics tenancy unit, so the routes are gated by the Analytics feature flag (503
/// when compiled out) and admins can manage any client's apps from the <c>/Apps</c> console instead.
/// </summary>
public static class RSAppManagementEndpoints
{
    public sealed record AppRequest(string? Slug, string? DisplayName);

    public sealed record AppResponse(
        string ClientSlug, string Slug, string DisplayName, bool Archived);

    private static AppResponse Map(RSCAppRecord a) =>
        new(a.ClientSlug, a.Slug, a.DisplayName, a.IsArchived);

    public static IEndpointRouteBuilder MapAppManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/apps")
            .RequireAuthorization(RSEndpointConventions.ApiKeyPolicy)
            .WithTags("Apps");

        // Feature gate: app management only matters when analytics is compiled in.
        group.AddEndpointFilter(async (ctx, next) =>
            RSCFeatureFlags.Analytics
                ? await next(ctx)
                : Results.Problem(RSCFeatureFlags.DisabledMessage, statusCode: RSCFeatureFlags.DisabledStatusCode));

        group.MapGet("", async (HttpContext ctx, RSCICatalog catalog, CancellationToken ct) =>
            {
                if (ClientOf(ctx) is not { } client) return NotAClientKey();
                var apps = await catalog.ListAppsAsync(client, includeArchived: false, ct);
                return Results.Ok(apps.Select(Map).ToList());
            })
            .WithName("ListMyApps")
            .WithSummary("List the authenticated client's apps")
            .WithDescription("Returns the active apps owned by the client the access key is bound to.")
            .Produces<IReadOnlyList<AppResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("", async (AppRequest body, HttpContext ctx, RSCICatalog catalog, RSCIAuditLog audit, CancellationToken ct) =>
            {
                if (ClientOf(ctx) is not { } client) return NotAClientKey();
                try
                {
                    var created = await catalog.CreateAppAsync(
                        client, body.Slug ?? string.Empty, body.DisplayName ?? string.Empty, ct);
                    await audit.RecordAsync(ctx, "app.create", success: true, target: $"{client}/{created.Slug}");
                    return Results.Created($"/api/v2/apps/{created.Slug}", Map(created));
                }
                catch (RSCCatalogException ex)
                {
                    await audit.RecordAsync(ctx, "app.create", success: false, target: $"{client}/{body.Slug}", details: ex.Message);
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
                }
            })
            .WithName("RegisterMyApp")
            .WithSummary("Register one of the authenticated client's apps")
            .WithDescription("Body: { slug, displayName? }. The slug is the immutable appId the SDK sends; it must be unique within your client. Fold any environment distinction into the slug (e.g. app-a-qa / app-a-prod). Returns 400 on a bad or duplicate slug.")
            .Accepts<AppRequest>("application/json")
            .Produces<AppResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/{slug}", async (string slug, HttpContext ctx, RSCICatalog catalog, RSCIAuditLog audit, CancellationToken ct) =>
            {
                if (ClientOf(ctx) is not { } client) return NotAClientKey();
                var archived = await catalog.ArchiveAppAsync(client, slug, ct);
                await audit.RecordAsync(ctx, "app.archive", success: archived, target: $"{client}/{slug}");
                return archived ? Results.Ok(new { slug, archived = true }) : Results.NotFound();
            })
            .WithName("ArchiveMyApp")
            .WithSummary("Archive one of the authenticated client's apps")
            .WithDescription("Archives the app (it stops being a valid attribution target). Returns 404 if the client has no active app with that slug.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>The catalog client the request's key is bound to, or null for a root/unbound key.</summary>
    private static string? ClientOf(HttpContext ctx)
    {
        var value = ctx.User?.FindFirst(RSCTenantClaims.ClientId)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static IResult NotAClientKey() => Results.Problem(
        "This API key is not bound to a client, so it can't manage apps. Use a client access key (minted on the /Clients admin page).",
        statusCode: StatusCodes.Status403Forbidden);
}
