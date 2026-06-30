using System.Globalization;
using Microsoft.Data.Sqlite;
using ReportService.Models;

namespace ReportService.Analytics;

public sealed partial class RSCSqliteAnalyticsStore
{
    // -------- Ingestion --------

    public async Task<RSCAnalyticsBatchReceipt> WriteBatchAsync(
        RSCAnalyticsBatch batch,
        string? anonymousIdHash,
        string? clientIdHash,
        RSCAnalyticsValidationResult verdict,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        var receivedIso = ToIso(receivedAt);
        var generatedIso = ParseTolerant(batch.GeneratedAt) is { } g ? ToIso(g) : receivedIso;

        // Tenancy stamp for every row this batch produces. The ingestion layer resolves + validates
        // these before calling; here we coalesce null → the default tenant (matching RSCM005's
        // backfill) so direct callers (seeder, tests) need not set them.
        var appId = ResolveTenant(batch.AppId, DefaultAppId);
        var environment = ResolveTenant(batch.Environment, DefaultEnvironment);
        var clientId = ResolveTenant(batch.ClientId, DefaultClientId);

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);

            // 1) Accepted events FIRST. INSERT OR IGNORE leaves UNIQUE(platform, event_id) conflicts
            //    as silent no-ops — that's the idempotency contract. We count duplicates so the
            //    envelope row below can record the *actually inserted* count, not the pre-dedupe
            //    validator count. Without this ordering, retries inflate batch-summary metrics.
            int duplicates = 0;
            if (verdict.Accepted.Count > 0)
            {
                using var insertEvent = conn.CreateCommand();
                insertEvent.Transaction = tx;
                insertEvent.CommandTimeout = _commandTimeoutSeconds;
                insertEvent.CommandText = @"
INSERT OR IGNORE INTO analytics_events(
    event_id, batch_id, app_id, environment, client_id, platform, session_id, anonymous_id_hash, client_id_hash, hash_version,
    occurred_at, received_at, sequence, type, name, screen, feature, duration_ms,
    properties_json, items_json, aggregated_at)
VALUES(
    @event_id, @batch_id, @app_id, @environment, @client_id, @platform, @session_id, @anonymous_id_hash, @client_id_hash, @hash_version,
    @occurred_at, @received_at, @sequence, @type, @name, @screen, @feature, @duration_ms,
    @properties_json, @items_json, NULL);";

                var eventIdParam = insertEvent.Parameters.Add("@event_id", SqliteType.Text);
                var batchIdParam = insertEvent.Parameters.Add("@batch_id", SqliteType.Text);
                insertEvent.Parameters.AddWithValue("@app_id", appId);
                insertEvent.Parameters.AddWithValue("@environment", environment);
                insertEvent.Parameters.AddWithValue("@client_id", clientId);
                var platformParam = insertEvent.Parameters.Add("@platform", SqliteType.Text);
                var sessionIdParam = insertEvent.Parameters.Add("@session_id", SqliteType.Text);
                var anonParam = insertEvent.Parameters.Add("@anonymous_id_hash", SqliteType.Text);
                var clientParam = insertEvent.Parameters.Add("@client_id_hash", SqliteType.Text);
                var hashVersionParam = insertEvent.Parameters.Add("@hash_version", SqliteType.Integer);
                var occurredParam = insertEvent.Parameters.Add("@occurred_at", SqliteType.Text);
                var receivedParam = insertEvent.Parameters.Add("@received_at", SqliteType.Text);
                var sequenceParam = insertEvent.Parameters.Add("@sequence", SqliteType.Integer);
                var typeParam = insertEvent.Parameters.Add("@type", SqliteType.Text);
                var nameParam = insertEvent.Parameters.Add("@name", SqliteType.Text);
                var screenParam = insertEvent.Parameters.Add("@screen", SqliteType.Text);
                var featureParam = insertEvent.Parameters.Add("@feature", SqliteType.Text);
                var durationParam = insertEvent.Parameters.Add("@duration_ms", SqliteType.Integer);
                var propsParam = insertEvent.Parameters.Add("@properties_json", SqliteType.Text);
                var itemsParam = insertEvent.Parameters.Add("@items_json", SqliteType.Text);

                batchIdParam.Value = batch.BatchId;
                platformParam.Value = (batch.Platform ?? string.Empty).ToLowerInvariant();
                anonParam.Value = (object?)anonymousIdHash ?? DBNull.Value;
                clientParam.Value = (object?)clientIdHash ?? DBNull.Value;
                hashVersionParam.Value = _hashVersion;
                receivedParam.Value = receivedIso;

                foreach (var ev in verdict.Accepted)
                {
                    eventIdParam.Value = ev.EventId;
                    sessionIdParam.Value = ev.SessionId;
                    occurredParam.Value = ToIso(ev.OccurredAt);
                    sequenceParam.Value = ev.Sequence;
                    typeParam.Value = ev.Type;
                    nameParam.Value = ev.Name;
                    screenParam.Value = (object?)ev.Screen ?? DBNull.Value;
                    featureParam.Value = (object?)ev.Feature ?? DBNull.Value;
                    durationParam.Value = ev.DurationMs.HasValue ? (object)ev.DurationMs.Value : DBNull.Value;
                    propsParam.Value = RSCAnalyticsValidator.SerializeProperties(ev.Properties);
                    itemsParam.Value = RSCAnalyticsValidator.SerializeItems(ev.Items);

                    var affected = await insertEvent.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                    if (affected == 0) duplicates++;
                }
            }

            // 2) Envelope row. accepted_count is the post-dedupe value so batch summary metrics
            //    don't inflate when an SDK retries (whether under the same batch_id or, as in the
            //    pre-fix mobile clients, under a fresh batch_id with the same events). ON CONFLICT
            //    DO UPDATE picks max() of new and existing for accepted_count so a true replay of
            //    the same batch_id never lowers the recorded contribution. The conflict target is
            //    the full tenant+platform-scoped key (RSCM006) so a batch_id reused across tenants
            //    is two distinct envelopes, not one silently-merged row.
            int actualAccepted = verdict.Accepted.Count - duplicates;
            using (var envelope = conn.CreateCommand())
            {
                envelope.Transaction = tx;
                envelope.CommandTimeout = _commandTimeoutSeconds;
                envelope.CommandText = @"
INSERT INTO analytics_batches(
    batch_id, received_at, generated_at, app_id, environment, client_id, platform, sdk_version, host_app_version,
    schema_version, anonymous_id_hash, client_id_hash, hash_version,
    accepted_count, rejected_count, batch_rejected, batch_reject_reason)
VALUES(
    @batch_id, @received_at, @generated_at, @app_id, @environment, @client_id, @platform, @sdk_version, @host_app_version,
    @schema_version, @anonymous_id_hash, @client_id_hash, @hash_version,
    @accepted, @rejected, @batch_rejected, @batch_reject_reason)
ON CONFLICT(app_id, environment, client_id, platform, batch_id) DO UPDATE SET
    accepted_count      = MAX(analytics_batches.accepted_count, excluded.accepted_count),
    rejected_count      = MAX(analytics_batches.rejected_count, excluded.rejected_count),
    batch_rejected      = excluded.batch_rejected,
    batch_reject_reason = excluded.batch_reject_reason;";
                envelope.Parameters.AddWithValue("@batch_id", batch.BatchId);
                envelope.Parameters.AddWithValue("@received_at", receivedIso);
                envelope.Parameters.AddWithValue("@app_id", appId);
                envelope.Parameters.AddWithValue("@environment", environment);
                envelope.Parameters.AddWithValue("@client_id", clientId);
                envelope.Parameters.AddWithValue("@generated_at", generatedIso);
                envelope.Parameters.AddWithValue("@platform", (batch.Platform ?? string.Empty).ToLowerInvariant());
                envelope.Parameters.AddWithValue("@sdk_version", batch.SdkVersion ?? string.Empty);
                envelope.Parameters.AddWithValue("@host_app_version", (object?)batch.HostAppVersion ?? DBNull.Value);
                envelope.Parameters.AddWithValue("@schema_version", batch.SchemaVersion);
                envelope.Parameters.AddWithValue("@anonymous_id_hash", (object?)anonymousIdHash ?? DBNull.Value);
                envelope.Parameters.AddWithValue("@client_id_hash", (object?)clientIdHash ?? DBNull.Value);
                envelope.Parameters.AddWithValue("@hash_version", _hashVersion);
                envelope.Parameters.AddWithValue("@accepted", actualAccepted);
                envelope.Parameters.AddWithValue("@rejected", verdict.Rejected.Count);
                envelope.Parameters.AddWithValue("@batch_rejected", verdict.BatchRejected ? 1 : 0);
                envelope.Parameters.AddWithValue("@batch_reject_reason", (object?)verdict.BatchRejectReason ?? DBNull.Value);

                await envelope.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }

            // 3) Rejected events. DLQ is append-only — every retry adds rows. The rotation worker
            //    trims them by age.
            if (verdict.Rejected.Count > 0)
            {
                using var insertDlq = conn.CreateCommand();
                insertDlq.Transaction = tx;
                insertDlq.CommandTimeout = _commandTimeoutSeconds;
                insertDlq.CommandText = @"
INSERT INTO analytics_dead_letters(received_at, batch_id, app_id, environment, client_id, platform, event_id, reason, detail, raw_json)
VALUES(@received_at, @batch_id, @app_id, @environment, @client_id, @platform, @event_id, @reason, @detail, @raw_json);";

                var receivedParam = insertDlq.Parameters.Add("@received_at", SqliteType.Text);
                var batchIdParam = insertDlq.Parameters.Add("@batch_id", SqliteType.Text);
                insertDlq.Parameters.AddWithValue("@app_id", appId);
                insertDlq.Parameters.AddWithValue("@environment", environment);
                insertDlq.Parameters.AddWithValue("@client_id", clientId);
                var platformParam = insertDlq.Parameters.Add("@platform", SqliteType.Text);
                var eventIdParam = insertDlq.Parameters.Add("@event_id", SqliteType.Text);
                var reasonParam = insertDlq.Parameters.Add("@reason", SqliteType.Text);
                var detailParam = insertDlq.Parameters.Add("@detail", SqliteType.Text);
                var rawParam = insertDlq.Parameters.Add("@raw_json", SqliteType.Text);

                receivedParam.Value = receivedIso;
                batchIdParam.Value = batch.BatchId;
                platformParam.Value = (batch.Platform ?? string.Empty).ToLowerInvariant();

                foreach (var r in verdict.Rejected)
                {
                    eventIdParam.Value = (object?)r.EventId ?? DBNull.Value;
                    reasonParam.Value = r.Reason;
                    detailParam.Value = (object?)r.Detail ?? DBNull.Value;
                    rawParam.Value = RedactRawForDeadLetter(r.Reason, r.RawJson);
                    await insertDlq.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(innerCt).ConfigureAwait(false);

            return new RSCAnalyticsBatchReceipt(
                BatchId: batch.BatchId,
                AcceptedCount: verdict.Accepted.Count - duplicates,
                RejectedCount: verdict.Rejected.Count,
                DuplicateCount: duplicates,
                BatchRejected: verdict.BatchRejected,
                BatchRejectReason: verdict.BatchRejectReason);
        }, ct).ConfigureAwait(false);
    }

    private const string PiiRedactionMarker = "[redacted]";

    /// <summary>
    /// Picks the redaction strategy for a dead-letter row's raw JSON. Every row is scrubbed before
    /// it lands at rest, so a forbidden value can never reach the durable, age-trimmed table:
    /// <list type="bullet">
    /// <item><see cref="RSCAnalyticsDeadLetterReasons.PiiKeyForbidden"/> — the event tripped the PII
    /// guard, so we know it carries a forbidden value but not necessarily under a recognised key
    /// (the guard fired before normalisation). Collapse <em>every</em> scalar to the marker.</item>
    /// <item>Any other reason — including the batch-level rejects (<c>platform_unknown</c>,
    /// <c>app_unknown</c>, <c>environment_unknown</c>, <c>client_unknown</c>, schema/clock-skew)
    /// whose raw JSON is captured by <c>RejectAll</c> <em>without</em> the per-event PII guard having
    /// run — scrub only the values under a forbidden key, keeping everything else for debuggability.
    /// </item>
    /// </list>
    /// </summary>
    private string RedactRawForDeadLetter(string reason, string rawJson) =>
        string.Equals(reason, RSCAnalyticsDeadLetterReasons.PiiKeyForbidden, StringComparison.Ordinal)
            ? RedactForbiddenPiiValues(rawJson)
            : RedactForbiddenKeyValues(rawJson, _forbiddenKeys);

    /// <summary>
    /// Rewrites raw JSON so the value of any property whose name matches the forbidden deny-list
    /// (case-insensitive, at any depth) is collapsed to the opaque marker, while every other value
    /// is preserved for debuggability. This is the defence-in-depth scrub for dead-letter rows that
    /// did <em>not</em> reach the per-event PII guard — e.g. a <c>platform_unknown</c> batch whose
    /// events still carry a <c>password</c> property. If the input can't be parsed as JSON we fall
    /// back to a fully-opaque marker rather than risk persisting the original.
    /// </summary>
    private static string RedactForbiddenKeyValues(string rawJson, HashSet<string> forbiddenKeys)
    {
        if (forbiddenKeys.Count == 0) return rawJson;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            using var buffer = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                WriteWithForbiddenKeysRedacted(doc.RootElement, writer, forbiddenKeys);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return PiiRedactionMarker;
        }
    }

    private static void WriteWithForbiddenKeysRedacted(
        System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer, HashSet<string> forbiddenKeys)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    // A forbidden key's value is collapsed wholesale (its subtree could itself hide
                    // PII); any other property recurses so non-forbidden values stay intact.
                    if (forbiddenKeys.Contains(prop.Name.ToLowerInvariant()))
                        writer.WriteStringValue(PiiRedactionMarker);
                    else
                        WriteWithForbiddenKeysRedacted(prop.Value, writer, forbiddenKeys);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteWithForbiddenKeysRedacted(item, writer, forbiddenKeys);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Rewrites a rejected event's raw JSON so no scalar value survives, while preserving the
    /// object/array structure and all property names for debuggability. Used for
    /// <see cref="RSCAnalyticsDeadLetterReasons.PiiKeyForbidden"/> rows so a forbidden PII value is
    /// never written to the durable dead-letter table. If the input can't be parsed as JSON we fall
    /// back to a fully-opaque marker rather than risk persisting the original.
    /// </summary>
    private static string RedactForbiddenPiiValues(string rawJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            using var buffer = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                WriteRedacted(doc.RootElement, writer);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return PiiRedactionMarker;
        }
    }

    private static void WriteRedacted(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteRedacted(prop.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer);
                }
                writer.WriteEndArray();
                break;
            case System.Text.Json.JsonValueKind.Null:
                // A null value carries no PII and keeping it preserves the original shape.
                writer.WriteNullValue();
                break;
            default:
                // Strings, numbers, and booleans are all potential PII carriers — collapse every
                // scalar to the opaque marker so nothing reversible is persisted.
                writer.WriteStringValue(PiiRedactionMarker);
                break;
        }
    }
}
