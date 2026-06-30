using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Integration tests against the real SQLite store: one isolated DB per test, asserts idempotency,
/// dead-letter handling, and the unaggregated→aggregated lifecycle the worker depends on.
/// </summary>
public class AnalyticsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;

    public AnalyticsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rs-analytics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        _analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-test.db",
            IdentifierHashPepper = "pepper-test",
        };
        _store = new RSCSqliteAnalyticsStore(_reportOptions, _analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        _validator = new RSCAnalyticsValidator(_analyticsOptions, _reportOptions, RSATestCatalog.Permissive, new ReportService.Options.RSCCatalogOptions());
        _hasher = new RSCAnalyticsIdentifierHasher(_analyticsOptions);
    }

    private RSCAnalyticsBatch MakeBatch(string platform = "android", params RSCAnalyticsEvent[] events) =>
        new(SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(), Platform: platform,
            SdkVersion: "1.0.0", HostAppVersion: "5.6.7", AnonymousId: "anon-1",
            ClientId: null, GeneratedAt: DateTimeOffset.UtcNow.ToString("O"), Events: events);

    private static RSCAnalyticsEvent MakeEvent(string eventId, string sessionId = "session-1",
        string type = "screen", DateTimeOffset? at = null) =>
        new(EventId: eventId, SessionId: sessionId, Sequence: 0,
            OccurredAt: (at ?? DateTimeOffset.UtcNow).ToString("O"),
            Type: type, Name: "home", Screen: "home", Feature: null, DurationMs: 100,
            Properties: new Dictionary<string, string>(), Items: null);

    [Fact]
    public async Task Accepted_events_are_written_and_idempotent_on_event_id()
    {
        var ev = MakeEvent("evt-1");
        var batch1 = MakeBatch("android", ev);
        var verdict1 = _validator.Validate(batch1, DateTimeOffset.UtcNow);
        var receipt1 = await _store.WriteBatchAsync(batch1, _hasher.Hash("anon-1"), null, verdict1, DateTimeOffset.UtcNow, default);
        Assert.Equal(1, receipt1.AcceptedCount);
        Assert.Equal(0, receipt1.DuplicateCount);

        // Replay the same event in a fresh batch with a new batchId — UNIQUE(platform, event_id)
        // should treat it as a duplicate, not a fresh row.
        var batch2 = MakeBatch("android", ev);
        var verdict2 = _validator.Validate(batch2, DateTimeOffset.UtcNow);
        var receipt2 = await _store.WriteBatchAsync(batch2, _hasher.Hash("anon-1"), null, verdict2, DateTimeOffset.UtcNow, default);
        Assert.Equal(0, receipt2.AcceptedCount);
        Assert.Equal(1, receipt2.DuplicateCount);

        var unaggregated = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Single(unaggregated);
    }

    [Fact]
    public async Task Rejected_events_land_in_dlq_visible_through_health_snapshot()
    {
        var bad = MakeEvent("evt-1") with
        {
            Properties = new Dictionary<string, string> { ["password"] = "hunter2" }
        };
        var good = MakeEvent("evt-2");
        var batch = MakeBatch("android", bad, good);
        var verdict = _validator.Validate(batch, DateTimeOffset.UtcNow);
        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null, verdict, DateTimeOffset.UtcNow, default);

        var health = await _store.GetHealthSnapshotAsync(10, default);
        Assert.Equal(1, health.DeadLetterTotal);
        Assert.Contains(RSCAnalyticsDeadLetterReasons.PiiKeyForbidden, health.DeadLettersByReason.Keys);
        Assert.Single(health.RecentSamples);
    }

    [Fact]
    public async Task Batch_level_reject_never_writes_a_forbidden_value_to_the_dlq()
    {
        // A batch-level reject (here platform_unknown) short-circuits the per-event PII guard:
        // RejectAll captures the whole event JSON, so a forbidden property would otherwise ride
        // along verbatim into the durable, age-trimmed dead-letter table. The store must scrub the
        // forbidden value regardless of the reject reason.
        var carriesPii = MakeEvent("evt-1") with
        {
            Properties = new Dictionary<string, string> { ["password"] = "hunter2", ["screen"] = "home" }
        };
        var batch = MakeBatch("windows", carriesPii); // not in AllowedPlatforms → platform_unknown
        var verdict = _validator.Validate(batch, DateTimeOffset.UtcNow);
        Assert.True(verdict.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PlatformUnknown, verdict.BatchRejectReason);

        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null, verdict, DateTimeOffset.UtcNow, default);

        var health = await _store.GetHealthSnapshotAsync(10, default);
        Assert.NotEmpty(health.RecentSamples);
        foreach (var sample in health.RecentSamples)
        {
            Assert.Equal(RSCAnalyticsDeadLetterReasons.PlatformUnknown, sample.Reason);
            // The forbidden value must be gone…
            Assert.DoesNotContain("hunter2", sample.RawJson);
            // …but the key and other (non-forbidden) values survive for debuggability.
            Assert.Contains("password", sample.RawJson);
            Assert.Contains("home", sample.RawJson);
        }
    }

    [Fact]
    public async Task Mark_events_aggregated_drops_them_from_the_unaggregated_pool()
    {
        var batch = MakeBatch("android", MakeEvent("evt-1"), MakeEvent("evt-2"));
        var verdict = _validator.Validate(batch, DateTimeOffset.UtcNow);
        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null, verdict, DateTimeOffset.UtcNow, default);

        var beforeMark = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Equal(2, beforeMark.Count);

        await _store.MarkEventsAggregatedAsync(new[]
        {
            new RSCAggregationEventRef("default", "production", "default", "android", "evt-1"),
            new RSCAggregationEventRef("default", "production", "default", "android", "evt-2")
        }, default);

        var afterMark = await _store.ListUnaggregatedEventsAsync(100, default);
        Assert.Empty(afterMark);
    }

    [Fact]
    public async Task Different_batch_ids_for_same_events_records_zero_inserted_on_replay()
    {
        // Pre-fix the mobile clients generated a fresh batch_id on every flush attempt. The
        // server's UNIQUE(platform, event_id) dedupes the events themselves, but the envelope
        // row used to record the pre-dedupe count — so replay traffic inflated batch summaries.
        // Now the envelope records the post-dedupe inserted count, and a replay's batch row
        // correctly shows zero inserts.
        var ev = MakeEvent("evt-replay");
        var first = MakeBatch("android", ev) with { BatchId = "batch-A" };
        var firstVerdict = _validator.Validate(first, DateTimeOffset.UtcNow);
        await _store.WriteBatchAsync(first, _hasher.Hash("anon"), null, firstVerdict, DateTimeOffset.UtcNow, default);

        var second = MakeBatch("android", ev) with { BatchId = "batch-B" };
        var secondVerdict = _validator.Validate(second, DateTimeOffset.UtcNow);
        await _store.WriteBatchAsync(second, _hasher.Hash("anon"), null, secondVerdict, DateTimeOffset.UtcNow, default);

        var summaries = await _store.GetPlatformSummariesAsync(default);
        var android = summaries.Single(s => s.Platform == "android");
        // Two envelope rows (different batch_ids), but only one actual event ever landed in
        // analytics_events. Aggregate accepted across batches should equal one, not two.
        Assert.Equal(2, android.Batches);
        Assert.Equal(1, android.AcceptedEvents);
    }

    [Fact]
    public async Task Same_batch_id_replay_keeps_original_accepted_count()
    {
        // Proper SDK retry (stable batch_id, same events). The envelope upsert's MAX(...) clause
        // keeps the recorded contribution from collapsing to zero on a duplicate-only retry.
        var batchId = "batch-stable";
        var ev = MakeEvent("evt-stable");
        var first = MakeBatch("android", ev) with { BatchId = batchId };
        await _store.WriteBatchAsync(first, _hasher.Hash("anon"), null,
            _validator.Validate(first, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, default);

        // Retry with the same batch_id and event_id — all duplicate, but envelope must not drop
        // to accepted=0 because the actual contribution was 1.
        var second = MakeBatch("android", ev) with { BatchId = batchId };
        await _store.WriteBatchAsync(second, _hasher.Hash("anon"), null,
            _validator.Validate(second, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, default);

        var summaries = await _store.GetPlatformSummariesAsync(default);
        var android = summaries.Single(s => s.Platform == "android");
        Assert.Equal(1, android.Batches);
        Assert.Equal(1, android.AcceptedEvents);
    }

    [Fact]
    public async Task Same_batch_id_across_two_clients_keeps_envelopes_separate()
    {
        // batch_id is client-generated and reused across retries, so two tenants can legitimately
        // emit the same value. Before RSCM006 the envelope PK was batch_id alone, so the second
        // tenant's batch collided and was folded into the first tenant's row (lost envelope +
        // inflated counts). With the tenant+platform-scoped key they stay two distinct envelopes.
        const string sharedBatchId = "shared-batch-id";
        var first = MakeBatch("android", MakeEvent("evt-c1")) with { BatchId = sharedBatchId, ClientId = "pharmacy-1" };
        await _store.WriteBatchAsync(first, _hasher.Hash("anon-1"), null,
            _validator.Validate(first, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, default);

        var second = MakeBatch("android", MakeEvent("evt-c2")) with { BatchId = sharedBatchId, ClientId = "pharmacy-2" };
        await _store.WriteBatchAsync(second, _hasher.Hash("anon-2"), null,
            _validator.Validate(second, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, default);

        var summaries = await _store.GetPlatformSummariesAsync(default);
        var android = summaries.Single(s => s.Platform == "android");
        // Two separate tenant envelopes (not one merged row), and both events landed.
        Assert.Equal(2, android.Batches);
        Assert.Equal(2, android.AcceptedEvents);
    }

    [Fact]
    public async Task Platform_summaries_count_batches_across_platforms()
    {
        await _store.WriteBatchAsync(MakeBatch("android", MakeEvent("a-1")),
            _hasher.Hash("anon-a"), null,
            _validator.Validate(MakeBatch("android", MakeEvent("a-1")), DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, default);
        await _store.WriteBatchAsync(MakeBatch("ios", MakeEvent("i-1")),
            _hasher.Hash("anon-i"), null,
            _validator.Validate(MakeBatch("ios", MakeEvent("i-1")), DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, default);

        var summaries = await _store.GetPlatformSummariesAsync(default);
        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.Platform == "android");
        Assert.Contains(summaries, s => s.Platform == "ios");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
