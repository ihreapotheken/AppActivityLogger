using System.Text.Json;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Validator-level tests against the canonical fixtures under <c>docs/analytics-contract/</c>.
/// SDK repos consume the same files: a change to <see cref="RSCAnalyticsBatch"/>,
/// <see cref="RSCAnalyticsEvent"/>, or <see cref="RSCAnalyticsItem"/> shape that doesn't update
/// the fixtures will break this suite first; SDK CI will then break in lock-step on its next run.
/// </summary>
public class AnalyticsContractFixtureTests
{
    private static readonly DateTimeOffset FixtureNow =
        new(2026, 5, 14, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string FixturesRoot = FindFixturesRoot();

    private static RSCAnalyticsValidator BuildValidator(RSCAnalyticsOptions? overrideOpts = null)
    {
        var opts = overrideOpts ?? new RSCAnalyticsOptions { IdentifierHashPepper = "fixture-pepper" };
        var report = new RSCReportServiceOptions { AllowedPlatforms = new[] { "android", "ios" } };
        return new RSCAnalyticsValidator(opts, report, RSATestCatalog.Permissive, new ReportService.Options.RSCCatalogOptions());
    }

    private static RSCAnalyticsBatch LoadBatch(string relativePath)
    {
        var full = Path.Combine(FixturesRoot, relativePath);
        Assert.True(File.Exists(full), $"fixture not found: {full}");
        var json = File.ReadAllText(full);
        var batch = JsonSerializer.Deserialize<RSCAnalyticsBatch>(json, DeserializeOptions);
        Assert.NotNull(batch);
        return batch!;
    }

    // ----- Accept fixtures -----

    [Theory]
    [InlineData("accept/screen.json",     1)]
    [InlineData("accept/action.json",     1)]
    [InlineData("accept/ecommerce.json",  1)]
    [InlineData("accept/engagement.json", 1)]
    [InlineData("accept/lifecycle.json",  2)]
    [InlineData("accept/derived.json",    1)]
    [InlineData("accept/error.json",      1)]
    [InlineData("accept/multi_event.json", 4)]
    public void Accept_fixture_is_accepted_in_full(string relativePath, int expectedAccepted)
    {
        var batch = LoadBatch(relativePath);
        var v = BuildValidator();
        var verdict = v.Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected, $"{relativePath}: batch unexpectedly rejected ({verdict.BatchRejectReason})");
        Assert.Empty(verdict.Rejected);
        Assert.Equal(expectedAccepted, verdict.Accepted.Count);
    }

    [Fact]
    public void Accept_ecommerce_fixture_carries_items()
    {
        var batch = LoadBatch("accept/ecommerce.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.Single(verdict.Accepted);
        var ev = verdict.Accepted[0];
        Assert.Equal(2, ev.Items.Count);
        Assert.Equal("sku-aspirin-500", ev.Items[0].ItemId);
        Assert.Equal("EUR", ev.Items[0].Currency);
    }

    // ----- Reject fixtures -----
    // One InlineData per documented dead-letter reason. The expected scope (batch-level vs.
    // per-event) and any required options overrides are encoded alongside.

    [Fact]
    public void Reject_schema_version_unsupported_is_batch_level()
    {
        var batch = LoadBatch("reject/schema_version_unsupported.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.True(verdict.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.SchemaVersionUnsupported, verdict.BatchRejectReason);
        Assert.Empty(verdict.Accepted);
    }

    [Fact]
    public void Reject_empty_batch_is_batch_level()
    {
        var batch = LoadBatch("reject/empty_batch.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.True(verdict.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.EmptyBatch, verdict.BatchRejectReason);
    }

    [Fact]
    public void Reject_platform_unknown_is_batch_level()
    {
        var batch = LoadBatch("reject/platform_unknown.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.True(verdict.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PlatformUnknown, verdict.BatchRejectReason);
    }

    [Fact]
    public void Reject_pii_key_is_per_event()
    {
        var batch = LoadBatch("reject/pii_key_forbidden.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PiiKeyForbidden, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_duplicate_event_id_is_per_event()
    {
        var batch = LoadBatch("reject/duplicate_event_id.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Accepted);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.DuplicateEventId, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_missing_required_field_is_per_event()
    {
        var batch = LoadBatch("reject/missing_required_field.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.MissingRequiredField, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_type_unknown_is_per_event()
    {
        var batch = LoadBatch("reject/type_unknown.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.TypeUnknown, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_invalid_timestamp_is_per_event()
    {
        var batch = LoadBatch("reject/invalid_timestamp.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.InvalidTimestamp, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_clock_skew_is_per_event()
    {
        // Default MaxClockSkewSeconds = 86400; the fixture's occurredAt is in 2099, so any
        // reasonable FixtureNow trips it.
        var batch = LoadBatch("reject/clock_skew.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.ClockSkew, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_property_count_exceeded_is_per_event()
    {
        var batch = LoadBatch("reject/property_count_exceeded.json");
        var verdict = BuildValidator().Validate(batch, FixtureNow);

        Assert.False(verdict.BatchRejected);
        Assert.Single(verdict.Rejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.PropertyCountExceeded, verdict.Rejected[0].Reason);
    }

    [Fact]
    public void Reject_batch_too_large_is_batch_level()
    {
        // Fixture has 3 events; override MaxEventsPerBatch=2 so the validator trips on size.
        var batch = LoadBatch("reject/batch_too_large.json");
        var v = BuildValidator(new RSCAnalyticsOptions
        {
            IdentifierHashPepper = "fixture-pepper",
            MaxEventsPerBatch = 2
        });
        var verdict = v.Validate(batch, FixtureNow);

        Assert.True(verdict.BatchRejected);
        Assert.Equal(RSCAnalyticsDeadLetterReasons.BatchTooLarge, verdict.BatchRejectReason);
    }

    // ----- Event catalog sync -----

    [Fact]
    public void Forbidden_keys_catalog_is_in_sync_with_server_defaults()
    {
        var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesRoot, "..", "event-catalog.json")));
        var catalogKeys = catalog.RootElement.GetProperty("forbiddenPropertyKeys")
            .EnumerateArray().Select(e => e.GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var serverKeys = new RSCAnalyticsOptions().ForbiddenPropertyKeys
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(serverKeys, catalogKeys);
    }

    [Fact]
    public void Event_kinds_catalog_is_in_sync_with_server_kinds()
    {
        var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesRoot, "..", "event-catalog.json")));
        var catalogKinds = catalog.RootElement.GetProperty("eventKinds")
            .EnumerateArray().Select(e => e.GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var serverKinds = RSCAnalyticsEventKinds.Known.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(serverKinds, catalogKinds);
    }

    [Fact]
    public void Event_catalog_only_references_known_event_kinds()
    {
        var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesRoot, "..", "event-catalog.json")));
        foreach (var ev in catalog.RootElement.GetProperty("events").EnumerateArray())
        {
            var name = ev.GetProperty("name").GetString();
            var kind = ev.GetProperty("type").GetString();
            Assert.True(RSCAnalyticsEventKinds.Known.Contains(kind!),
                $"event '{name}' has unknown kind '{kind}'");
        }
    }

    [Fact]
    public void Every_documented_dead_letter_reason_has_a_fixture()
    {
        // Reasons we deliberately do not yet ship a fixture for. Keep this list small and named —
        // a new reason without a fixture should fail this test, not silently drift.
        var skip = new HashSet<string>(StringComparer.Ordinal)
        {
            // event_too_large is covered indirectly via property_too_large + property_count_exceeded;
            // the validator currently doesn't raise it on its own path.
            RSCAnalyticsDeadLetterReasons.EventTooLarge,
            // property_too_large requires a 2KB+ string; the SDK round-trip tests cover that and
            // it adds disproportionate noise to a wire-shape fixture.
            RSCAnalyticsDeadLetterReasons.PropertyTooLarge,
            // app/client validity is a server-side CATALOG-registration property, not a wire-shape
            // one — an SDK cannot determine it from a fixture. These are covered by the dedicated
            // tenancy validation tests (against the real catalog), not wire fixtures.
            RSCAnalyticsDeadLetterReasons.AppUnknown,
            RSCAnalyticsDeadLetterReasons.ClientUnknown,
        };

        var rejectDir = Path.Combine(FixturesRoot, "reject");
        var fixtureNames = Directory.GetFiles(rejectDir, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in typeof(RSCAnalyticsDeadLetterReasons).GetFields())
        {
            var reason = (string)field.GetRawConstantValue()!;
            if (skip.Contains(reason)) continue;
            Assert.True(fixtureNames.Contains(reason),
                $"missing reject fixture for dead-letter reason '{reason}' (expected {reason}.json)");
        }
    }

    private static string FindFixturesRoot()
    {
        // Walk up from the test binary's directory until we find docs/analytics-contract/fixtures.
        // Falls back to a clear error so a misconfigured CI fails loud rather than silently passing.
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && current is not null; i++)
        {
            var candidate = Path.Combine(current, "docs", "analytics-contract", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException(
            $"could not locate docs/analytics-contract/fixtures starting from {AppContext.BaseDirectory}");
    }
}
