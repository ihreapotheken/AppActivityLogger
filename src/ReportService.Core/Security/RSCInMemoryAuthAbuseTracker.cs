using System.Collections.Concurrent;
using ReportService.Options;

namespace ReportService.Security;

/// <summary>
/// Bounded in-memory <see cref="RSCIAuthAbuseTracker"/>. Used as the fallback when the SQLite-backed
/// tracker is unavailable — the fail-closed policy means we still track bans, just without the
/// cross-restart persistence guarantee. Capacity-bounded (evicts the oldest entry past the cap) so
/// a pathological attacker sending traffic from many distinct IPs can't exhaust memory.
/// </summary>
public sealed class RSCInMemoryAuthAbuseTracker : RSCIAuthAbuseTracker
{
    private const int MaxEntries = 10_000;

    private readonly RSCReportServiceOptions _options;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public RSCInMemoryAuthAbuseTracker(RSCReportServiceOptions options) => _options = options;

    public Task<AbuseDecision> CheckAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source)) return Task.FromResult(new AbuseDecision(false, 0));
        if (!_entries.TryGetValue(source, out var e)) return Task.FromResult(new AbuseDecision(false, 0));

        var now = DateTimeOffset.UtcNow;
        if (e.BannedUntil is { } until && until > now)
        {
            var retry = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
            return Task.FromResult(new AbuseDecision(true, retry));
        }
        return Task.FromResult(new AbuseDecision(false, 0));
    }

    public Task RecordFailureAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source)) return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddSeconds(-Math.Max(1, _options.AuthAbuseWindowSeconds));
        var threshold = Math.Max(1, _options.AuthAbuseMaxFailures);
        var banDuration = Math.Max(1, _options.AuthAbuseBanSeconds);

        _entries.AddOrUpdate(source,
            _ => new Entry(1, now, null),
            (_, existing) =>
            {
                if (existing.BannedUntil is { } existingBan && existingBan > now)
                    return existing with { BannedUntil = now.AddSeconds(banDuration) };
                var failures = existing.WindowStarted < windowStart ? 1 : existing.Failures + 1;
                var windowStarted = existing.WindowStarted < windowStart ? now : existing.WindowStarted;
                DateTimeOffset? bannedUntil = failures >= threshold ? now.AddSeconds(banDuration) : null;
                return new Entry(failures, windowStarted, bannedUntil);
            });

        // Lazy bound: if we blow past the cap, evict the LRU-ish oldest by window_started_at.
        if (_entries.Count > MaxEntries) EvictOverflow();
        return Task.CompletedTask;
    }

    public Task ClearAsync(string source, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(source)) _entries.TryRemove(source, out _);
        return Task.CompletedTask;
    }

    private void EvictOverflow()
    {
        var target = MaxEntries - 256;
        if (_entries.Count <= target) return;
        foreach (var kvp in _entries.OrderBy(kvp => kvp.Value.WindowStarted).Take(_entries.Count - target))
            _entries.TryRemove(kvp.Key, out _);
    }

    private sealed record Entry(int Failures, DateTimeOffset WindowStarted, DateTimeOffset? BannedUntil);
}
