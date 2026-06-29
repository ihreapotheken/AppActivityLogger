using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReportService.Audit;
using ReportService.Storage.ApiKeys;

namespace ReportService.Endpoints;

/// <summary>
/// Admin-only REST surface for managing API keys. Gated by <see cref="RSEndpointConventions.ApiKeyAdminPolicy"/>
/// — i.e. an admin-role key (or the static root key). The minted plaintext is returned exactly once
/// by <c>POST</c>; it's never retrievable afterwards (only its hash is stored).
/// </summary>
public static class RSApiKeyEndpoints
{
    public sealed record CreateKeyRequest(
        string? Role,
        string? Label,
        DateTimeOffset? ExpiresAt,
        int? ExpiresInDays,
        int? RateLimitPerMinute);

    public sealed record CreateKeyResponse(
        string Id,
        string Key,
        string Role,
        string? Label,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        int? RateLimitPerMinute);

    public static IEndpointRouteBuilder MapApiKeyManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/keys")
            .RequireAuthorization(RSEndpointConventions.ApiKeyAdminPolicy)
            .WithTags("API Keys");

        group.MapPost("", async (CreateKeyRequest body, HttpContext ctx, RSCIApiKeyStore store, RSCIAuditLog audit, CancellationToken ct) =>
            {
                var role = string.IsNullOrWhiteSpace(body.Role) ? RSCApiKeyRoles.User : body.Role!.Trim().ToLowerInvariant();
                if (!RSCApiKeyRoles.IsValid(role))
                    return Results.Problem($"role must be '{RSCApiKeyRoles.Admin}' or '{RSCApiKeyRoles.User}'", statusCode: StatusCodes.Status400BadRequest);

                // expiresAt wins if both are supplied; expiresInDays is the convenience form.
                DateTimeOffset? expiresAt = body.ExpiresAt
                    ?? (body.ExpiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : null);
                if (expiresAt is { } e && e <= DateTimeOffset.UtcNow)
                    return Results.Problem("expiry must be in the future", statusCode: StatusCodes.Status400BadRequest);
                if (body.RateLimitPerMinute is < 1)
                    return Results.Problem("rateLimitPerMinute must be >= 1 when set", statusCode: StatusCodes.Status400BadRequest);

                var actor = ctx.User?.Identity?.Name ?? "unknown";
                try
                {
                    var created = await store.CreateAsync(role, body.Label, expiresAt, body.RateLimitPerMinute, actor, ct);
                    await audit.RecordAsync(ctx, "apikey.create", success: true, target: created.Metadata.Id,
                        details: $"role={role} expires={expiresAt?.ToString("O") ?? "never"} rate={body.RateLimitPerMinute?.ToString() ?? "default"}");
                    var m = created.Metadata;
                    return Results.Created($"/api/v1/keys/{m.Id}", new CreateKeyResponse(
                        m.Id, created.PlaintextKey, m.Role, m.Label, m.CreatedAt, m.ExpiresAt, m.RateLimitPerMinute));
                }
                catch (InvalidOperationException ex)
                {
                    await audit.RecordAsync(ctx, "apikey.create", success: false, details: ex.Message);
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .WithName("CreateApiKey")
            .WithSummary("Mint a new API key (admin only)")
            .WithDescription("Body: { role: 'user'|'admin', label?, expiresAt? (ISO-8601) | expiresInDays?, rateLimitPerMinute? }. The plaintext `key` is returned ONCE — store it now; only its hash is persisted.")
            .Produces<CreateKeyResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("", async (RSCIApiKeyStore store, CancellationToken ct) =>
            {
                var keys = await store.ListAsync(ct);
                return Results.Ok(keys);
            })
            .WithName("ListApiKeys")
            .WithSummary("List API key metadata (admin only)")
            .WithDescription("Returns metadata for every key (id, role, label, timestamps, expiry, revocation, per-key limit). Never returns hashes or plaintext.")
            .Produces<IReadOnlyList<RSCApiKeyMetadata>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("/{id}", async (string id, HttpContext ctx, RSCIApiKeyStore store, RSCIAuditLog audit, CancellationToken ct) =>
            {
                bool revoked;
                try
                {
                    revoked = await store.RevokeAsync(id, ctx.User?.Identity?.Name ?? "unknown", ct);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                await audit.RecordAsync(ctx, "apikey.revoke", success: revoked, target: id);
                return revoked ? Results.Ok(new { id, revoked = true }) : Results.NotFound();
            })
            .WithName("RevokeApiKey")
            .WithSummary("Revoke an API key by id (admin only)")
            .WithDescription("Revokes the key immediately (subsequent auth with it returns 401). Returns 404 if no active key with that id exists.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
