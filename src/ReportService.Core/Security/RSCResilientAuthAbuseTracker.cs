using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Security;

/// <summary>
/// Fail-closed wrapper around the SQLite <see cref="RSCIAuthAbuseTracker"/>. Policy when the SQLite
/// backend is unavailable: swap in the in-memory tracker so bans KEEP WORKING (protected endpoints
/// never lose brute-force protection), log loudly, and mark <see cref="RSCComponentHealth"/> degraded
/// so the admin dashboard surfaces the fallback. Cross-restart persistence is the cost of running
/// in fallback — preferable to silently skipping the check and letting a brute-force storm through.
/// </summary>
public sealed class RSCResilientAuthAbuseTracker : RSCIAuthAbuseTracker
{
    public const string Component = "AuthAbuseDb";
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);

    private readonly Func<RSCIAuthAbuseTracker> _primaryFactory;
    private readonly RSCIAuthAbuseTracker _fallback;
    private readonly RSCComponentHealth _health;
    private readonly ILogger<RSCResilientAuthAbuseTracker> _logger;

    private readonly object _gate = new();
    private RSCIAuthAbuseTracker? _primary;
    private DateTimeOffset _nextConstructAttempt = DateTimeOffset.MinValue;

    public RSCResilientAuthAbuseTracker(
        Func<RSCIAuthAbuseTracker> primaryFactory,
        RSCReportServiceOptions options,
        RSCComponentHealth health,
        ILogger<RSCResilientAuthAbuseTracker> logger)
    {
        _primaryFactory = primaryFactory;
        _fallback = new RSCInMemoryAuthAbuseTracker(options);
        _health = health;
        _logger = logger;
        _ = GetPrimary("startup");
    }

    public Task<AbuseDecision> CheckAsync(string source, CancellationToken ct)
        => CallAsync(t => t.CheckAsync(source, ct), "check");

    public Task RecordFailureAsync(string source, CancellationToken ct)
        => CallAsync(async t => { await t.RecordFailureAsync(source, ct).ConfigureAwait(false); return 0; }, "record");

    public Task ClearAsync(string source, CancellationToken ct)
        => CallAsync(async t => { await t.ClearAsync(source, ct).ConfigureAwait(false); return 0; }, "clear");

    private async Task<T> CallAsync<T>(Func<RSCIAuthAbuseTracker, Task<T>> body, string op)
    {
        var primary = GetPrimary(op);
        if (primary is not null)
        {
            try
            {
                var result = await body(primary).ConfigureAwait(false);
                _health.MarkHealthy(Component);
                return result;
            }
            catch (Exception ex)
            {
                _health.MarkDegraded(Component, $"{op} failed, falling back to in-memory tracker: {ex.Message}", ex);
                _logger.LogError(ex,
                    "Auth-abuse {Op} failed on SQLite backend — falling back to bounded in-memory tracker. " +
                    "Bans will stop surviving restarts until the DB is back.", op);
            }
        }

        return await body(_fallback).ConfigureAwait(false);
    }

    // Overload returning Task (not Task<int>); used by RecordFailure/Clear so we don't allocate a sentinel.
    private Task CallAsync(Func<RSCIAuthAbuseTracker, Task> body, string op)
        => CallAsync<int>(async t => { await body(t).ConfigureAwait(false); return 0; }, op);

    private RSCIAuthAbuseTracker? GetPrimary(string op)
    {
        if (_primary is not null) return _primary;
        lock (_gate)
        {
            if (_primary is not null) return _primary;
            if (DateTimeOffset.UtcNow < _nextConstructAttempt) return null;

            try
            {
                _primary = _primaryFactory();
                _health.MarkHealthy(Component, "sqlite tracker constructed");
                return _primary;
            }
            catch (Exception ex)
            {
                _nextConstructAttempt = DateTimeOffset.UtcNow + RetryCooldown;
                _health.MarkDegraded(Component, $"construction failed during {op}: {ex.Message}", ex);
                _logger.LogError(ex,
                    "SQLite auth-abuse tracker unavailable (op={Op}); serving from bounded in-memory fallback, retry after {Cooldown}",
                    op, RetryCooldown);
                return null;
            }
        }
    }
}
