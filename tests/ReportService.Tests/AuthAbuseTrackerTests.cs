using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Security;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies the persisted auth-abuse tracker: failures accumulate inside the sliding window, a ban
/// kicks in once the threshold is crossed, a successful auth clears the counter, and state survives a
/// "restart" (opening a new tracker instance against the same database file).
/// </summary>
public class AuthAbuseTrackerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RSCReportServiceOptions _options;

    public AuthAbuseTrackerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"abuse-test-{Guid.NewGuid():N}.db");
        _options = new RSCReportServiceOptions
        {
            AuthAbuseDbPath = _dbPath,
            AuthAbuseMaxFailures = 3,
            AuthAbuseWindowSeconds = 60,
            AuthAbuseBanSeconds = 120,
            SqliteCommandTimeoutSeconds = 5
        };
    }

    public void Dispose()
    {
        foreach (var extra in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + extra); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Ban_applies_once_threshold_crossed()
    {
        var tracker = new RSCSqliteAuthAbuseTracker(_options, NullLogger<RSCSqliteAuthAbuseTracker>.Instance);

        Assert.False((await tracker.CheckAsync("1.2.3.4", default)).IsBanned);

        await tracker.RecordFailureAsync("1.2.3.4", default);
        await tracker.RecordFailureAsync("1.2.3.4", default);
        Assert.False((await tracker.CheckAsync("1.2.3.4", default)).IsBanned);

        await tracker.RecordFailureAsync("1.2.3.4", default);
        var decision = await tracker.CheckAsync("1.2.3.4", default);
        Assert.True(decision.IsBanned);
        Assert.InRange(decision.RetryAfterSeconds, 1, _options.AuthAbuseBanSeconds);
    }

    [Fact]
    public async Task Success_clears_failures()
    {
        var tracker = new RSCSqliteAuthAbuseTracker(_options, NullLogger<RSCSqliteAuthAbuseTracker>.Instance);
        await tracker.RecordFailureAsync("9.9.9.9", default);
        await tracker.RecordFailureAsync("9.9.9.9", default);
        await tracker.ClearAsync("9.9.9.9", default);

        // After clearing, two more failures should NOT yet ban (threshold is 3).
        await tracker.RecordFailureAsync("9.9.9.9", default);
        await tracker.RecordFailureAsync("9.9.9.9", default);
        Assert.False((await tracker.CheckAsync("9.9.9.9", default)).IsBanned);
    }

    [Fact]
    public async Task State_survives_process_restart()
    {
        var first = new RSCSqliteAuthAbuseTracker(_options, NullLogger<RSCSqliteAuthAbuseTracker>.Instance);
        for (var i = 0; i < _options.AuthAbuseMaxFailures; i++)
            await first.RecordFailureAsync("2.2.2.2", default);

        Assert.True((await first.CheckAsync("2.2.2.2", default)).IsBanned);

        // Simulate restart: brand-new instance pointing at the same file.
        var second = new RSCSqliteAuthAbuseTracker(_options, NullLogger<RSCSqliteAuthAbuseTracker>.Instance);
        var decision = await second.CheckAsync("2.2.2.2", default);
        Assert.True(decision.IsBanned);
        Assert.True(decision.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task Isolated_sources_do_not_affect_each_other()
    {
        var tracker = new RSCSqliteAuthAbuseTracker(_options, NullLogger<RSCSqliteAuthAbuseTracker>.Instance);
        for (var i = 0; i < _options.AuthAbuseMaxFailures; i++)
            await tracker.RecordFailureAsync("10.0.0.1", default);

        Assert.True((await tracker.CheckAsync("10.0.0.1", default)).IsBanned);
        Assert.False((await tracker.CheckAsync("10.0.0.2", default)).IsBanned);
    }

    [Fact]
    public async Task Concurrent_failures_cannot_drop_updates()
    {
        // With a threshold of 3, 50 concurrent failures must still produce a ban — the previous
        // read-modify-write could drop increments and let an attacker stay under the limit.
        var tracker = new RSCSqliteAuthAbuseTracker(_options, NullLogger<RSCSqliteAuthAbuseTracker>.Instance);

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => tracker.RecordFailureAsync("5.5.5.5", default)))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.True((await tracker.CheckAsync("5.5.5.5", default)).IsBanned);
    }
}
