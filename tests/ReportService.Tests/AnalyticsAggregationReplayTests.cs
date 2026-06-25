using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Asserts the atomic-per-tick fix on the aggregation worker: a crash inside the tick rolls
/// back the upserts + the mark step together, so the next tick replays the same events as if
/// the first never happened. Without the fix, partial upserts would survive and the replay
/// would double session/daily counters.
/// </summary>
public class AnalyticsAggregationReplayTests : IDisposable
{
    private readonly string _root;
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCAnalyticsAggregationWorker _worker;

    public AnalyticsAggregationReplayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rs-analytics-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        _analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-replay.db",
            IdentifierHashPepper = "pepper-test"
        };
        _store = new RSCSqliteAnalyticsStore(_reportOptions, _analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        _validator = new RSCAnalyticsValidator(_analyticsOptions, _reportOptions);
        _hasher = new RSCAnalyticsIdentifierHasher(_analyticsOptions);
        _worker = new RSCAnalyticsAggregationWorker(_store, _analyticsOptions, NullLogger<RSCAnalyticsAggregationWorker>.Instance);
    }

    private RSCAnalyticsBatch MakeBatch(string platform, params RSCAnalyticsEvent[] events) =>
        new(SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(), Platform: platform,
            SdkVersion: "1.0.0", HostAppVersion: "5.6.7", AnonymousId: "anon-1",
            ClientId: null, GeneratedAt: DateTimeOffset.UtcNow.ToString("O"), Events: events);

    private static RSCAnalyticsEvent MakeEvent(string eventId, string sessionId, string type, DateTimeOffset at) =>
        new(EventId: eventId, SessionId: sessionId, Sequence: 0,
            OccurredAt: at.ToString("O"),
            Type: type, Name: "home", Screen: "home", Feature: null, DurationMs: 100,
            Properties: new Dictionary<string, string>(), Items: null);

    [Fact]
    public async Task Tick_records_one_contribution_then_leaves_nothing_to_aggregate()
    {
        var now = DateTimeOffset.UtcNow;
        var batch = MakeBatch("android",
            MakeEvent("evt-1", "session-A", "screen", now),
            MakeEvent("evt-2", "session-A", "action", now),
            MakeEvent("evt-3", "session-A", "screen", now));

        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null,
            _validator.Validate(batch, now), now, default);

        var firstProcessed = await _worker.TickAsync(default);
        Assert.Equal(3, firstProcessed);

        // A second tick over the same input must process nothing — events are marked aggregated
        // in the same transaction as the upserts, so they leave the unaggregated pool the moment
        // the rollups are written. No double-count exposure.
        var secondProcessed = await _worker.TickAsync(default);
        Assert.Equal(0, secondProcessed);

        var (eventCount, screenCount) = await ReadSessionCountersAsync("android", "session-A");
        Assert.Equal(3, eventCount);
        Assert.Equal(2, screenCount);

        var dailyEvents = await ReadDailyRollupEventsAsync("android", DateOnly.FromDateTime(now.UtcDateTime));
        Assert.Equal(3, dailyEvents);
    }

    [Fact]
    public async Task A_failed_tick_leaves_the_unaggregated_pool_intact()
    {
        // Drive WriteAggregationTickAsync directly with deliberately-bad input so the SQLite
        // transaction throws and rolls back. Verifies the atomic boundary: nothing in
        // analytics_sessions, nothing in analytics_daily_rollups, no events marked aggregated.
        var now = DateTimeOffset.UtcNow;
        var batch = MakeBatch("android",
            MakeEvent("evt-A", "session-A", "screen", now),
            MakeEvent("evt-B", "session-A", "screen", now));

        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null,
            _validator.Validate(batch, now), now, default);

        var preCrash = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Equal(2, preCrash.Count);

        // Craft a tick that succeeds on the session/user-day/daily upserts but fails on the
        // mark step — the mark step references an event_id with an embedded NULL byte SQLite
        // can store but the foreach loop uses the same parameter object, so the last UPDATE
        // hits a constraint we can't trip cleanly here. Cheaper route: supply an event id that
        // doesn't exist plus a sentinel that causes SqliteCommand to throw on parameter type
        // mismatch.
        //
        // The simplest fault injection: inject a daily-rollup delta with a null required field.
        // SQLite rejects NULL on a NOT NULL column, the transaction rolls back, the exception
        // propagates. The session/user-day rows already inserted in the same tx are reverted.
        var badTick = new RSCAnalyticsAggregationTick(
            Sessions: new[]
            {
                new RSCAggregationSessionDelta(
                    Platform: "android", SessionId: "session-A",
                    AnonymousIdHash: _hasher.Hash("anon-1"),
                    StartedAt: now, LastSeenAt: now,
                    EventCount: 2, ScreenCount: 2)
            },
            UserDays: new[]
            {
                new RSCAggregationUserDayDelta(
                    Platform: "android",
                    Day: DateOnly.FromDateTime(now.UtcDateTime),
                    AnonymousIdHash: _hasher.Hash("anon-1")!,
                    HashVersion: 1, Events: 2)
            },
            DailyRollups: new[]
            {
                // platform = null trips a NOT NULL constraint inside the same transaction —
                // sessions/user-day upserts above are rolled back atomically.
                new RSCAggregationDailyRollupDelta(
                    Day: DateOnly.FromDateTime(now.UtcDateTime),
                    Platform: null!,
                    Events: 2, Sessions: 1, DistinctUsers: 1)
            },
            EventIds: new[] { "evt-A", "evt-B" });

        await Assert.ThrowsAnyAsync<Exception>(() => _store.WriteAggregationTickAsync(badTick, default));

        // Nothing visible: the transaction rolled back.
        var sessions = await _store.ListSessionsAsync("android", 100, 0, default);
        Assert.Empty(sessions);

        var rollups = await _store.GetDailyRollupsAsync(
            now.AddDays(-1), now.AddDays(1), "android", default);
        Assert.Empty(rollups);

        // The events are still unaggregated — ready for the next tick to retry.
        var stillUnaggregated = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Equal(2, stillUnaggregated.Count);
    }

    [Fact]
    public async Task Incremental_ticks_accumulate_session_and_daily_counters()
    {
        var now = DateTimeOffset.UtcNow;
        var b1 = MakeBatch("android",
            MakeEvent("evt-1", "session-A", "screen", now),
            MakeEvent("evt-2", "session-A", "screen", now));
        await _store.WriteBatchAsync(b1, _hasher.Hash("anon-1"), null,
            _validator.Validate(b1, now), now, default);
        await _worker.TickAsync(default);

        var b2 = MakeBatch("android",
            MakeEvent("evt-3", "session-A", "action", now),
            MakeEvent("evt-4", "session-A", "screen", now),
            MakeEvent("evt-5", "session-A", "screen", now));
        await _store.WriteBatchAsync(b2, _hasher.Hash("anon-1"), null,
            _validator.Validate(b2, now), now, default);
        await _worker.TickAsync(default);

        var (eventCount, screenCount) = await ReadSessionCountersAsync("android", "session-A");
        Assert.Equal(5, eventCount);
        Assert.Equal(4, screenCount);

        var dailyEvents = await ReadDailyRollupEventsAsync("android", DateOnly.FromDateTime(now.UtcDateTime));
        Assert.Equal(5, dailyEvents);
    }

    private async Task<(long Events, long Screens)> ReadSessionCountersAsync(string platform, string sessionId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _store.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_count, screen_count FROM analytics_sessions WHERE platform = @p AND session_id = @s;";
        cmd.Parameters.AddWithValue("@p", platform);
        cmd.Parameters.AddWithValue("@s", sessionId);
        using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "session row missing");
        return (r.GetInt64(0), r.GetInt64(1));
    }

    private async Task<long> ReadDailyRollupEventsAsync(string platform, DateOnly day)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _store.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT events FROM analytics_daily_rollups WHERE day = @d AND platform = @p;";
        cmd.Parameters.AddWithValue("@d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@p", platform);
        var raw = await cmd.ExecuteScalarAsync();
        return raw is null ? 0 : Convert.ToInt64(raw);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
