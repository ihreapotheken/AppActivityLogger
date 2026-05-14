namespace ReportService.Security;

/// <summary>Abuse-tracker decision for one source (typically a client IP).</summary>
public readonly record struct AbuseDecision(bool IsBanned, int RetryAfterSeconds);

/// <summary>
/// Per-source failure tracker with a sliding window and temporary ban. Must be concurrency-safe and
/// persist state so process restarts don't reset an attacker's counter.
/// </summary>
public interface RSCIAuthAbuseTracker
{
    Task<AbuseDecision> CheckAsync(string source, CancellationToken ct);
    Task RecordFailureAsync(string source, CancellationToken ct);
    Task ClearAsync(string source, CancellationToken ct);
}
