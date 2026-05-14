using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReportService.Security;

/// <summary>
/// Authentication handler that validates a shared API key supplied via the
/// <see cref="RSApiKeyAuthenticationOptions.HeaderName"/> header.
/// </summary>
public sealed class RSApiKeyAuthenticationHandler : AuthenticationHandler<RSApiKeyAuthenticationOptions>
{
    private readonly RSCIAuthAbuseTracker _abuse;

    /// <summary>
    /// Constructs the handler with the framework-provided options, logger, URL encoder, and the
    /// persisted auth abuse tracker used to throttle repeated failures from the same source IP.
    /// </summary>
    public RSApiKeyAuthenticationHandler(
        IOptionsMonitor<RSApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        RSCIAuthAbuseTracker abuse)
        : base(options, logger, encoder)
    {
        _abuse = abuse;
    }

    /// <summary>
    /// Validates the request's API key header via <see cref="RSCSecretComparer"/> (constant-time). An
    /// absent header yields <see cref="AuthenticateResult.NoResult"/>; a present-but-mismatched
    /// header records a failure against the persisted <see cref="RSCIAuthAbuseTracker"/>, which soft-
    /// bans the source IP once repeated failures cross the configured threshold.
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

        if (string.IsNullOrEmpty(Options.ExpectedKey))
        {
            return AuthenticateResult.Fail("API key not configured on server");
        }

        if (!Request.Headers.TryGetValue(RSApiKeyAuthenticationOptions.HeaderName, out var values) || values.Count != 1)
        {
            return AuthenticateResult.NoResult();
        }

        if (!RSCSecretComparer.Matches(values.ToString(), Options.ExpectedKey))
        {
            await _abuse.RecordFailureAsync(source, Context.RequestAborted).ConfigureAwait(false);
            return AuthenticateResult.Fail("Invalid API key");
        }

        await _abuse.ClearAsync(source, Context.RequestAborted).ConfigureAwait(false);

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "sdk-client") },
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
