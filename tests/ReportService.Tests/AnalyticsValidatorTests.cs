using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

public class AnalyticsValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static RSCAnalyticsValidator Build(RSCAnalyticsOptions? overrideOpts = null)
    {
        var opts = overrideOpts ?? new RSCAnalyticsOptions();
        var report = new RSCReportServiceOptions { AllowedPlatforms = new[] { "android", "ios" } };
        return new RSCAnalyticsValidator(opts, report);
    }

    private static RSCAnalyticsBatch MakeBatch(params RSCAnalyticsEvent[] events) =>
        new(SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(), Platform: "android",
            SdkVersion: "1.0.0", HostAppVersion: "5.6.7", AnonymousId: "anon-1",
            ClientId: null, GeneratedAt: Now.ToString("O"), Events: events);

    private static RSCAnalyticsEvent MakeEvent(string? type = "screen", string? name = "home", string? eventId = null) =>
        new(EventId: eventId ?? Guid.NewGuid().ToString(),
            SessionId: "session-1", Sequence: 0, OccurredAt: Now.ToString("O"),
            Type: type ?? "screen", Name: name ?? "home",
            Screen: "home", Feature: null, DurationMs: 100,
            Properties: new Dictionary<string, string>(), Items: null);

    [Fact]
    public void Empty_batch_is_rejected()
    {
        var v = Build();
        var batch = MakeBatch();
        var r = v.Validate(batch, Now);
        Assert.True(r.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.EmptyBatch, r.BatchRejectReason);
    }

    [Fact]
    public void Unknown_schema_version_dlqs_every_event()
    {
        var v = Build();
        var batch = MakeBatch(MakeEvent(), MakeEvent()) with { SchemaVersion = 99 };
        var r = v.Validate(batch, Now);
        Assert.True(r.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.SchemaVersionUnsupported, r.BatchRejectReason);
        Assert.Equal(2, r.Rejected.Count);
        Assert.Empty(r.Accepted);
    }

    [Fact]
    public void Unknown_platform_is_rejected()
    {
        var v = Build();
        var batch = MakeBatch(MakeEvent()) with { Platform = "windows" };
        var r = v.Validate(batch, Now);
        Assert.True(r.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PlatformUnknown, r.BatchRejectReason);
    }

    [Fact]
    public void Forbidden_property_keys_are_individually_rejected()
    {
        var v = Build();
        var badEvent = MakeEvent() with
        {
            Properties = new Dictionary<string, string> { ["email"] = "a@b.c" }
        };
        var batch = MakeBatch(MakeEvent(), badEvent);
        var r = v.Validate(batch, Now);
        Assert.False(r.BatchRejected);
        Assert.Single(r.Accepted);
        Assert.Single(r.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PiiKeyForbidden, r.Rejected[0].Reason);
    }

    [Fact]
    public void Duplicate_event_ids_within_one_batch_are_rejected()
    {
        var v = Build();
        var dup = "dup-event";
        var batch = MakeBatch(MakeEvent(eventId: dup), MakeEvent(eventId: dup));
        var r = v.Validate(batch, Now);
        Assert.Single(r.Accepted);
        Assert.Single(r.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.DuplicateEventId, r.Rejected[0].Reason);
    }

    [Fact]
    public void Clock_skew_outside_window_is_rejected()
    {
        var v = Build(new RSCAnalyticsOptions { MaxClockSkewSeconds = 60 });
        var future = MakeEvent() with { OccurredAt = Now.AddHours(1).ToString("O") };
        var batch = MakeBatch(future);
        var r = v.Validate(batch, Now);
        Assert.Single(r.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.ClockSkew, r.Rejected[0].Reason);
    }

    [Fact]
    public void Clock_skew_in_the_past_is_also_rejected()
    {
        // CODE-REVIEW finding #34: the skew check is symmetric — |occurredAt - receivedAt| — so a
        // legitimately backfilled event (occurredAt 2 days ago) is rejected the same way a future
        // one is. Pins the symmetric window so a future backfill allowance turns this red.
        var v = Build(new RSCAnalyticsOptions { MaxClockSkewSeconds = 60 });
        var past = MakeEvent() with { OccurredAt = Now.AddDays(-2).ToString("O") };
        var batch = MakeBatch(past);
        var r = v.Validate(batch, Now);
        Assert.Single(r.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.ClockSkew, r.Rejected[0].Reason);
    }

    [Fact]
    public void Too_many_events_rejects_batch()
    {
        var v = Build(new RSCAnalyticsOptions { MaxEventsPerBatch = 2 });
        var batch = MakeBatch(MakeEvent(), MakeEvent(), MakeEvent());
        var r = v.Validate(batch, Now);
        Assert.True(r.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.BatchTooLarge, r.BatchRejectReason);
    }
}
