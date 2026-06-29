using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Options;
using ReportService.Storage.ApiKeys;

namespace ReportService.Security;

/// <summary>
/// Authentication handler for the <c>apiKey</c> header. Resolves the presented key through
/// <see cref="RSCApiKeyResolver"/> — the permanent static root key first, then managed DB keys
/// (admin/user, with expiry + revocation). On success the principal carries the key id as both
/// <see cref="ClaimTypes.NameIdentifier"/> and <see cref="ClaimTypes.Name"/> (so the audit log
/// attributes actions to the key) plus a <see cref="ClaimTypes.Role"/> claim that gates the
/// admin-only key-management endpoints.
/// </summary>
public sealed class RSApiKeyAuthenticationHandler : AuthenticationHandler<RSApiKeyAuthenticationOptions>
{
    private readonly RSCIAuthAbuseTracker _abuse;
    private readonly RSCReportServiceOptions _serviceOptions;
    private readonly RSCIApiKeyStore _keyStore;

    public RSApiKeyAuthenticationHandler(
        IOptionsMonitor<RSApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        RSCIAuthAbuseTracker abuse,
        RSCReportServiceOptions serviceOptions,
        RSCIApiKeyStore keyStore)
        : base(options, logger, encoder)
    {
        _abuse = abuse;
        _serviceOptions = serviceOptions;
        _keyStore = keyStore;
    }

    /// <summary>
    /// An absent header yields <see cref="AuthenticateResult.NoResult"/>. A present-but-unresolvable
    /// header (unknown, expired, or revoked key) records a failure against the persisted
    /// <see cref="RSCIAuthAbuseTracker"/>, which soft-bans the source IP once repeated failures cross
    /// the configured threshold.
    /// </summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var source = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Fast-path the ban check: if this source is already banned, fail without a key compare.
        var decision = await _abuse.CheckAsync(source, Context.RequestAborted).ConfigureAwait(false);
        if (decision.IsBanned)
        {
            Context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
            Logger.LogWarning("Rejected request from banned source {Source} (retry after {Seconds}s)", source, decision.RetryAfterSeconds);
            return AuthenticateResult.Fail("source temporarily blocked");
        }

        if (!Request.Headers.TryGetValue(RSApiKeyAuthenticationOptions.HeaderName, out var values) || values.Count != 1)
        {
            return AuthenticateResult.NoResult();
        }

        var resolution = RSCApiKeyResolver.Resolve(values.ToString(), _serviceOptions, _keyStore);
        if (resolution is null)
        {
            await _abuse.RecordFailureAsync(source, Context.RequestAborted).ConfigureAwait(false);
            return AuthenticateResult.Fail("Invalid API key");
        }

        await _abuse.ClearAsync(source, Context.RequestAborted).ConfigureAwait(false);

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, resolution.KeyId),
                new Claim(ClaimTypes.Name, resolution.KeyId),
                new Claim(ClaimTypes.Role, resolution.Role),
            },
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Writes a <c>401</c> response with a <c>WWW-Authenticate</c> header advertising the <c>ApiKey</c> scheme.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers["WWW-Authenticate"] = $"{RSApiKeyAuthenticationOptions.Scheme} realm=\"report-service\"";
        return Task.CompletedTask;
    }
}
