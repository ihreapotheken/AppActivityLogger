using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers CODE-REVIEW finding #14/#20 (HIGH): RSCSqliteAnalyticsStore.PurgeOlderThanAsync. The
/// fix added an <c>aggregated_at IS NOT NULL</c> guard to the events delete so a backed-up
/// aggregator can never have its un-folded events trimmed, while the DLQ delete runs on its own
/// independent cutoff. Real SQLite store, one isolated DB per test, no mocks. The aggregation
/// worker is driven directly (TickAsync) to fold events before purge.
/// </summary>
public class AnalyticsRetentionPurgeTests : IDisposable
{
    private readonly string _root;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCAnalyticsAggregationWorker _worker;

    public AnalyticsRetentionPurgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rs-analytics-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        var analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-purge.db",
            IdentifierHashPepper = "pepper-test"
        };
        _store = new RSCSqliteAnalyticsStore(reportOptions, analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        _validator = new RSCAnalyticsValidator(analyticsOptions, reportOptions);
        _hasher = new RSCAnalyticsIdentifierHasher(analyticsOptions);
        _worker = new RSCAnalyticsAggregationWorker(_store, analyticsOptions, NullLogger<RSCAnalyticsAggregationWorker>.Instance);
    }

    private RSCAnalyticsBatch MakeBatch(string platform, params RSCAnalyticsEvent[] events) =>
        new(SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(), Platform: platform,
            SdkVersion: "1.0.0", HostAppVersion: "5.6.7", AnonymousId: "anon-1",
            ClientId: null, GeneratedAt: DateTimeOffset.UtcNow.ToString("O"), Events: events);

    // occurredAt is pinned near receivedAt so the symmetric clock-skew check (|occurredAt - receivedAt|)
    // always passes regardless of how far in the past receivedAt is set.
    private static RSCAnalyticsEvent MakeEvent(string eventId, DateTimeOffset receivedAt) =>
        new(EventId: eventId, SessionId: "session-A", Sequence: 0,
            OccurredAt: receivedAt.ToString("O"),
            Type: "screen", Name: "home", Screen: "home", Feature: null, DurationMs: 100,
            Properties: new Dictionary<string, string>(), Items: null);

    private async Task WriteAsync(RSCAnalyticsBatch batch, DateTimeOffset receivedAt)
    {
        var verdict = _validator.Validate(batch, receivedAt);
        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null, verdict, receivedAt, default);
    }

    [Fact]
    public async Task Aggregated_events_older_than_cutoff_are_deleted_and_counted()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await WriteAsync(MakeBatch("android", MakeEvent("evt-old-1", old), MakeEvent("evt-old-2", old)), old);

        // Fold them so aggregated_at is set — the purge only trims folded rows.
        var processed = await _worker.TickAsync(default);
        Assert.Equal(2, processed);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var deleted = await _store.PurgeOlderThanAsync(cutoff, cutoff, default);
        Assert.Equal(2, deleted);

        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task Unaggregated_events_older_than_cutoff_are_preserved()
    {
        // The whole point of the fix: an event past the cutoff that the aggregator has NOT yet
        // folded must survive the purge so a backed-up aggregator can still roll it up later.
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await WriteAsync(MakeBatch("android", MakeEvent("evt-unagg", old)), old);

        // Deliberately do NOT run the worker — the row's aggregated_at stays NULL.
        var beforePurge = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Single(beforePurge);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var deleted = await _store.PurgeOlderThanAsync(cutoff, cutoff, default);
        Assert.Equal(0, deleted);

        var afterPurge = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Single(afterPurge);
        Assert.Equal("evt-unagg", afterPurge[0].EventId);
    }

    [Fact]
    public async Task Dead_letters_older_than_their_cutoff_are_purged()
    {
        // A forbidden PII key dead-letters the event. Write it with an old receivedAt so it falls
        // before the DLQ cutoff.
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        var bad = MakeEvent("evt-dlq", old) with
        {
            Properties = new Dictionary<string, string> { ["email"] = "a@b.c" }
        };
        await WriteAsync(MakeBatch("android", bad), old);

        var before = await _store.GetHealthSnapshotAsync(10, default);
        Assert.Equal(1, before.DeadLetterTotal);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var deleted = await _store.PurgeOlderThanAsync(cutoff, cutoff, default);
        Assert.Equal(1, deleted);

        var after = await _store.GetHealthSnapshotAsync(10, default);
        Assert.Equal(0, after.DeadLetterTotal);
    }

    [Fact]
    public async Task Daily_rollups_are_not_touched_by_the_purge()
    {
        // Roll up two old events, purge the raw events, and assert the rollup row survives — purge
        // only trims raw events + dead letters, never the derived rollup tables.
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await WriteAsync(MakeBatch("android", MakeEvent("evt-r1", old), MakeEvent("evt-r2", old)), old);
        await _worker.TickAsync(default);

        var rollupsBefore = await _store.GetDailyRollupsAsync(old.AddDays(-1), old.AddDays(1), "android", default);
        Assert.NotEmpty(rollupsBefore);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await _store.PurgeOlderThanAsync(cutoff, cutoff, default);

        var rollupsAfter = await _store.GetDailyRollupsAsync(old.AddDays(-1), old.AddDays(1), "android", default);
        Assert.NotEmpty(rollupsAfter);
    }

    private async Task<long> CountEventsAsync()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _store.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analytics_events;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
