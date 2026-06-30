using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// One-shot migration utility: scans the existing problem-report store for entries with
/// <c>Kind == "analytics"</c> and folds them into the v2 analytics pipeline. Lets operators turn
/// historical analytics demo data into rollups without re-flowing it from the SDKs.
/// </summary>
/// <remarks>
/// Idempotent on (platform, event_id). Re-running the importer over the same source files only
/// inserts events that aren't already present. Once every legacy row has been migrated and the
/// SDKs are on the v2 path, this service can be deleted — it's intentionally not wired as a
/// recurring background task.
/// </remarks>
public sealed class RSAAnalyticsLegacyImporter
{
    private const string LegacyChannel = "legacy-import";
    private const string LegacySdkVersion = "legacy-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
    };

    private readonly RSCIReportStore _reportStore;
    private readonly RSCIAnalyticsStore _analyticsStore;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly ILogger<RSAAnalyticsLegacyImporter> _logger;

    public RSAAnalyticsLegacyImporter(
        RSCIReportStore reportStore,
        RSCIAnalyticsStore analyticsStore,
        RSCAnalyticsValidator validator,
        RSCAnalyticsIdentifierHasher hasher,
        RSCReportServiceOptions reportOptions,
        ILogger<RSAAnalyticsLegacyImporter> logger)
    {
        _reportStore = reportStore;
        _analyticsStore = analyticsStore;
        _validator = validator;
        _hasher = hasher;
        _reportOptions = reportOptions;
        _logger = logger;
    }

    public async Task<RSAAnalyticsImportReport> ImportAsync(CancellationToken ct)
    {
        int scanned = 0, converted = 0, skippedNonAnalytics = 0, failed = 0;

        foreach (var platform in _reportOptions.AllowedPlatforms)
        {
            foreach (var stored in _reportStore.List(platform))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                try
                {
                    using var stream = _reportStore.OpenRead(platform, stored.FileName);
                    if (stream is null) continue;

                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { skippedNonAnalytics++; continue; }

                    var kind = GetString(root, "Kind") ?? GetString(root, "kind");
                    if (!string.Equals(kind, "analytics", StringComparison.Ordinal))
                    {
                        skippedNonAnalytics++;
                        continue;
                    }

                    // Project the legacy report shape onto a v2 batch. The session id is fabricated
                    // from the source filename so two events from the same file land on the same
                    // session — the only signal we have without true session info.
                    //
                    // The event_id MUST be deterministic from the source identity, otherwise the
                    // store's INSERT OR IGNORE on UNIQUE(platform, event_id) can never match a
                    // prior run and re-importing the same files double-counts every event (see
                    // the class remark — idempotency is a documented contract). One legacy file
                    // maps to exactly one synthesized event, so the file name is a stable key.
                    var eventId = $"legacy-{stored.Platform}-{stored.FileName}";
                    var occurredAt = GetString(root, "OccurredAt") ?? stored.SubmittedAt.UtcDateTime.ToString("O");
                    var screen = GetString(root, "Source");
                    var userId = GetString(root, "UserId");
                    var props = ExtractEventProperties(root);

                    var batch = new RSCAnalyticsBatch(
                        SchemaVersion: 1,
                        BatchId: $"legacy-{stored.Platform}-{stored.FileName}",
                        Platform: stored.Platform,
                        SdkVersion: LegacySdkVersion,
                        HostAppVersion: GetString(root, "AppVersion"),
                        AnonymousId: userId,
                        ClientId: GetString(root, "PharmacyId"),
                        GeneratedAt: stored.SubmittedAt.UtcDateTime.ToString("O"),
                        Events: new[]
                        {
                            new RSCAnalyticsEvent(
                                EventId: eventId,
                                SessionId: $"legacy-{stored.FileName}",
                                Sequence: 0,
                                OccurredAt: occurredAt,
                                Type: RSCAnalyticsEventKinds.Screen,
                                Name: GetString(root, "Title") ?? "legacy_analytics",
                                Screen: screen,
                                Feature: LegacyChannel,
                                DurationMs: null,
                                Properties: props,
                                Items: null)
                        });

                    var verdict = _validator.Validate(batch, DateTimeOffset.UtcNow);
                    var anonHash = _hasher.Hash(batch.AnonymousId);
                    // clientId is stored verbatim as the tenancy key, not hashed; the legacy
                    // client_id_hash column is left unpopulated. See RSAnalyticsIngestionService.
                    var receipt = await _analyticsStore.WriteBatchAsync(batch, anonHash, clientIdHash: null, verdict,
                        DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

                    // Count only events that were genuinely inserted this run. Because the
                    // synthesized event_id is deterministic, a second import over the same files
                    // is dropped as a UNIQUE(platform, event_id) conflict (receipt.AcceptedCount
                    // is post-dedupe), so re-runs report converted=0 instead of masking the
                    // no-op as fresh conversions.
                    if (!verdict.BatchRejected)
                        converted += receipt.AcceptedCount;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Legacy analytics import failed for {Platform}/{File}",
                        platform, stored.FileName);
                    failed++;
                }
            }
        }

        var report = new RSAAnalyticsImportReport(scanned, converted, skippedNonAnalytics, failed);
        _logger.LogInformation(
            "Legacy analytics import complete: scanned={Scanned} converted={Converted} skipped={Skipped} failed={Failed}",
            report.Scanned, report.Converted, report.Skipped, report.Failed);
        return report;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static Dictionary<string, string> ExtractEventProperties(JsonElement root)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("EventProperties", out var ep) || ep.ValueKind != JsonValueKind.Object)
            return props;

        foreach (var p in ep.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                var value = p.Value.GetString() ?? string.Empty;
                // Keep keys short and conservative — anything pathological is what the validator
                // is for. Names with whitespace are normalized.
                var key = p.Name.Replace(' ', '_');
                props[key] = value;
            }
        }
        return props;
    }
}

/// <summary>Summary of one legacy-import run, surfaced on the maintenance page.</summary>
public sealed record RSAAnalyticsImportReport(int Scanned, int Converted, int Skipped, int Failed);
