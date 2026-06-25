using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Integration tests for the cohort + funnel workers' store plumbing. Builds a real SQLite
/// store, writes synthetic events and user-days, runs the recompute methods, and reads the
/// materialised tables back.
/// </summary>
public class AnalyticsFunnelAndCohortTests : IDisposable
{
    private readonly string _root;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCReportServiceOptions _reportOptions;

    public AnalyticsFunnelAndCohortTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rs-analytics-funnel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        _analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-funnel.db",
            IdentifierHashPepper = "pepper-test",
            MaxClockSkewSeconds = int.MaxValue / 2,
        };
        _store = new RSCSqliteAnalyticsStore(_reportOptions, _analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        _validator = new RSCAnalyticsValidator(_analyticsOptions, _reportOptions);
        _hasher = new RSCAnalyticsIdentifierHasher(_analyticsOptions);
    }

    [Fact]
    public async Task Retention_cohort_counts_d1_d7_d30_correctly()
    {
        // Hand-craft analytics_user_days rows via the worker path: write events with chosen
        // occurredAt timestamps and let the aggregation worker fold them into user-days.
        var aggWorker = new RSCAnalyticsAggregationWorker(_store, _analyticsOptions,
            NullLogger<RSCAnalyticsAggregationWorker>.Instance);

        var installDay = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-31);

        // User A: active on install_day, day+1, day+7, day+30. Should be counted in all three windows.
        await WriteEvent("user-A", installDay,           "evt-a-0");
        await WriteEvent("user-A", installDay.AddDays(1), "evt-a-1");
        await WriteEvent("user-A", installDay.AddDays(7), "evt-a-7");
        await WriteEvent("user-A", installDay.AddDays(30),"evt-a-30");

        // User B: active on install_day, day+1, day+7 but NOT day+30. D1 and D7 yes, D30 no.
        await WriteEvent("user-B", installDay,           "evt-b-0");
        await WriteEvent("user-B", installDay.AddDays(1), "evt-b-1");
        await WriteEvent("user-B", installDay.AddDays(7), "evt-b-7");

        // User C: install only. None of D1/D7/D30.
        await WriteEvent("user-C", installDay, "evt-c-0");

        await aggWorker.TickAsync(default);

        var worker = new RSCAnalyticsCohortWorker(_store, _analyticsOptions,
            NullLogger<RSCAnalyticsCohortWorker>.Instance);
        var cohortsTouched = await worker.TickAsync(default);
        Assert.True(cohortsTouched > 0);

        var rows = await _store.ListRetentionCohortsAsync("android", days: 60, default);
        var cohort = rows.Single(r => r.InstallDay == installDay);
        Assert.Equal(3, cohort.CohortSize);
        Assert.Equal(2, cohort.Day1Retained);
        Assert.Equal(2, cohort.Day7Retained);
        Assert.Equal(1, cohort.Day30Retained);
    }

    [Fact]
    public async Task Funnel_worker_records_step_observations_for_matching_sessions()
    {
        // Seed a funnel definition manually so we don't rely on the seed-on-start path.
        var def = new RSCAnalyticsFunnelDefinition(
            FunnelKey: "test_funnel",
            DisplayName: "test funnel",
            Steps: new[]
            {
                new RSCAnalyticsFunnelStep("search", "otc_search_submitted", "action"),
                new RSCAnalyticsFunnelStep("view",   "otc_product_view",     "screen"),
                new RSCAnalyticsFunnelStep("buy",    "purchase",             "ecommerce")
            },
            Enabled: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
        await _store.UpsertFunnelDefinitionAsync(def, default);

        var now = DateTimeOffset.UtcNow;

        // Session 1 reaches all three steps in order.
        await WriteNamedEvent("session-1", "evt-1a", "action",    "otc_search_submitted", now.AddSeconds(-30));
        await WriteNamedEvent("session-1", "evt-1b", "screen",    "otc_product_view",     now.AddSeconds(-20));
        await WriteNamedEvent("session-1", "evt-1c", "ecommerce", "purchase",             now.AddSeconds(-10));

        // Session 2 reaches steps 0 and 1 but doesn't purchase.
        await WriteNamedEvent("session-2", "evt-2a", "action", "otc_search_submitted", now.AddSeconds(-40));
        await WriteNamedEvent("session-2", "evt-2b", "screen", "otc_product_view",     now.AddSeconds(-30));

        // Session 3 only fires step 0.
        await WriteNamedEvent("session-3", "evt-3a", "action", "otc_search_submitted", now.AddSeconds(-50));

        // Session 4 has the events but in WRONG order (purchase before search) — should reach step 0 only.
        await WriteNamedEvent("session-4", "evt-4a", "ecommerce", "purchase",             now.AddSeconds(-60));
        await WriteNamedEvent("session-4", "evt-4b", "action",    "otc_search_submitted", now.AddSeconds(-50));

        var worker = new RSCAnalyticsFunnelWorker(_store, _analyticsOptions,
            NullLogger<RSCAnalyticsFunnelWorker>.Instance);
        var observed = await worker.TickAsync(default);
        Assert.True(observed > 0);

        var summary = await _store.GetFunnelSummaryAsync("test_funnel",
            now.AddDays(-1), now.AddDays(1), platform: null, default);
        var byStep = summary.ToDictionary(s => s.StepIndex, s => s.SessionsReached);

        Assert.Equal(4, byStep[0]); // every session reached step 0
        Assert.Equal(2, byStep[1]); // sessions 1, 2
        Assert.Equal(1, byStep[2]); // session 1 only
    }

    private async Task WriteEvent(string anonymousId, DateOnly day, string eventId)
    {
        var occurredAt = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var ev = new RSCAnalyticsEvent(
            EventId: eventId, SessionId: $"sess-{anonymousId}-{day:yyyyMMdd}",
            Sequence: 0,
            OccurredAt: occurredAt.ToString("O"),
            Type: "screen", Name: "home", Screen: "home", Feature: null, DurationMs: 100,
            Properties: new Dictionary<string, string>(), Items: null);
        var batch = new RSCAnalyticsBatch(
            SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(),
            Platform: "android", SdkVersion: "1.0.0", HostAppVersion: "5.6.7",
            AnonymousId: anonymousId, ClientId: null,
            GeneratedAt: occurredAt.ToString("O"),
            Events: new[] { ev });
        var verdict = _validator.Validate(batch, occurredAt);
        await _store.WriteBatchAsync(batch, _hasher.Hash(anonymousId), null, verdict, occurredAt, default);
    }

    private async Task WriteNamedEvent(string sessionId, string eventId, string type, string name, DateTimeOffset at)
    {
        var ev = new RSCAnalyticsEvent(
            EventId: eventId, SessionId: sessionId,
            Sequence: 0,
            OccurredAt: at.ToString("O"),
            Type: type, Name: name, Screen: null, Feature: null, DurationMs: null,
            Properties: new Dictionary<string, string>(), Items: null);
        var batch = new RSCAnalyticsBatch(
            SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(),
            Platform: "android", SdkVersion: "1.0.0", HostAppVersion: "5.6.7",
            AnonymousId: "anon-funnel", ClientId: null,
            GeneratedAt: at.ToString("O"),
            Events: new[] { ev });
        var verdict = _validator.Validate(batch, at);
        await _store.WriteBatchAsync(batch, _hasher.Hash("anon-funnel"), null, verdict, at, default);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
