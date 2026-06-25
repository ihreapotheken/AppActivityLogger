using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Migrations;
using ReportService.Storage.Migrations.Analytics;

namespace ReportService.Analytics;

/// <summary>
/// SQLite-backed implementation of <see cref="RSCIAnalyticsStore"/>. Lives in its own database
/// file (default <c>analytics.db</c>) so the analytics schema can evolve independently of the
/// problem-report index.
/// </summary>
public sealed class RSCSqliteAnalyticsStore : RSCIAnalyticsStore
{
    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    private const string IsoFormat = "O";

    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly int _commandTimeoutSeconds;
    private readonly int _hashVersion;
    private readonly ILogger<RSCSqliteAnalyticsStore> _logger;
    private int _schemaVersion;

    public string DbPath => _dbPath;
    public int SchemaVersion => _schemaVersion;

    public RSCSqliteAnalyticsStore(
        RSCReportServiceOptions reportOptions,
        RSCAnalyticsOptions analyticsOptions,
        ILogger<RSCSqliteAnalyticsStore> logger)
    {
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, analyticsOptions.SqliteCommandTimeoutSeconds);
        _hashVersion = analyticsOptions.IdentifierHashVersion;

        _dbPath = RSCStatePaths.Resolve(analyticsOptions.SqliteDbPath, reportOptions.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Bootstrap();
    }

    private static IEnumerable<RSCISchemaMigration> BuildMigrations() => new RSCISchemaMigration[]
    {
        new RSCM001_CreateAnalyticsTables(),
        new RSCM002_CreateRetentionAndFunnelTables(),
        new RSCM003_AddEventIdIndex(),
    };

    private void Bootstrap()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }

            var runner = new RSCSchemaRunner(BuildMigrations(), _logger);
            _schemaVersion = runner.Run(conn);

            _logger.LogInformation("SQLite analytics store ready at {Path} (schema v{Version})", _dbPath, _schemaVersion);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to bootstrap SQLite analytics store");
            throw;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteWithRetryAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        await ExecuteWithRetryAsync<object?>(async innerCt =>
        {
            await work(innerCt).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var delayMs = InitialBusyBackoffMs;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < MaxBusyRetries)
            {
                _logger.LogWarning(ex,
                    "Analytics SQLite busy on attempt {Attempt}/{Max}; retrying in {DelayMs}ms",
                    attempt, MaxBusyRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex, "Analytics SQLite operation failed (attempt {Attempt})", attempt);
                throw;
            }
        }
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static string ToIso(DateTimeOffset value) =>
        value.UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

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
    event_id, batch_id, platform, session_id, anonymous_id_hash, client_id_hash, hash_version,
    occurred_at, received_at, sequence, type, name, screen, feature, duration_ms,
    properties_json, items_json, aggregated_at)
VALUES(
    @event_id, @batch_id, @platform, @session_id, @anonymous_id_hash, @client_id_hash, @hash_version,
    @occurred_at, @received_at, @sequence, @type, @name, @screen, @feature, @duration_ms,
    @properties_json, @items_json, NULL);";

                var eventIdParam = insertEvent.Parameters.Add("@event_id", SqliteType.Text);
                var batchIdParam = insertEvent.Parameters.Add("@batch_id", SqliteType.Text);
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
            //    the same batch_id never lowers the recorded contribution.
            int actualAccepted = verdict.Accepted.Count - duplicates;
            using (var envelope = conn.CreateCommand())
            {
                envelope.Transaction = tx;
                envelope.CommandTimeout = _commandTimeoutSeconds;
                envelope.CommandText = @"
INSERT INTO analytics_batches(
    batch_id, received_at, generated_at, platform, sdk_version, host_app_version,
    schema_version, anonymous_id_hash, client_id_hash, hash_version,
    accepted_count, rejected_count, batch_rejected, batch_reject_reason)
VALUES(
    @batch_id, @received_at, @generated_at, @platform, @sdk_version, @host_app_version,
    @schema_version, @anonymous_id_hash, @client_id_hash, @hash_version,
    @accepted, @rejected, @batch_rejected, @batch_reject_reason)
ON CONFLICT(batch_id) DO UPDATE SET
    accepted_count      = MAX(analytics_batches.accepted_count, excluded.accepted_count),
    rejected_count      = MAX(analytics_batches.rejected_count, excluded.rejected_count),
    batch_rejected      = excluded.batch_rejected,
    batch_reject_reason = excluded.batch_reject_reason;";
                envelope.Parameters.AddWithValue("@batch_id", batch.BatchId);
                envelope.Parameters.AddWithValue("@received_at", receivedIso);
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
INSERT INTO analytics_dead_letters(received_at, batch_id, platform, event_id, reason, detail, raw_json)
VALUES(@received_at, @batch_id, @platform, @event_id, @reason, @detail, @raw_json);";

                var receivedParam = insertDlq.Parameters.Add("@received_at", SqliteType.Text);
                var batchIdParam = insertDlq.Parameters.Add("@batch_id", SqliteType.Text);
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
                    rawParam.Value = r.RawJson;
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

    private static DateTimeOffset? ParseTolerant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
    }

    // -------- Aggregation --------

    public async Task<IReadOnlyList<RSCAnalyticsStoredEvent>> ListUnaggregatedEventsAsync(
        int limit, CancellationToken ct)
    {
        if (limit <= 0) return Array.Empty<RSCAnalyticsStoredEvent>();

        const string sql = @"
SELECT event_id, platform, session_id, anonymous_id_hash, sequence, occurred_at, type, name,
       screen, feature, duration_ms
FROM analytics_events
WHERE aggregated_at IS NULL
ORDER BY id ASC
LIMIT @limit;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsStoredEvent>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@limit", limit);

            var result = new List<RSCAnalyticsStoredEvent>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                result.Add(new RSCAnalyticsStoredEvent(
                    EventId: reader.GetString(0),
                    Platform: reader.GetString(1),
                    SessionId: reader.GetString(2),
                    AnonymousIdHash: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Sequence: reader.GetInt64(4),
                    OccurredAt: ParseIso(reader.GetString(5)),
                    Type: reader.GetString(6),
                    Name: reader.GetString(7),
                    Screen: reader.IsDBNull(8) ? null : reader.GetString(8),
                    Feature: reader.IsDBNull(9) ? null : reader.GetString(9),
                    DurationMs: reader.IsDBNull(10) ? null : reader.GetInt64(10)));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task MarkEventsAggregatedAsync(IReadOnlyList<string> eventIds, CancellationToken ct)
    {
        if (eventIds.Count == 0) return;

        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "UPDATE analytics_events SET aggregated_at = @ts WHERE event_id = @event_id;";
            var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);
            var idParam = cmd.Parameters.Add("@event_id", SqliteType.Text);

            tsParam.Value = ToIso(DateTimeOffset.UtcNow);
            foreach (var id in eventIds)
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task WriteAggregationTickAsync(RSCAnalyticsAggregationTick tick, CancellationToken ct)
    {
        if (tick.EventIds.Count == 0 &&
            tick.Sessions.Count == 0 &&
            tick.UserDays.Count == 0 &&
            tick.DailyRollups.Count == 0)
        {
            return;
        }

        var nowIso = ToIso(DateTimeOffset.UtcNow);

        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);

            // 1) Sessions: SUM/MAX upsert. Safe inside this transaction because the per-event
            //    mark below either commits with these deltas or rolls back the whole lot.
            if (tick.Sessions.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
INSERT INTO analytics_sessions(platform, session_id, anonymous_id_hash, started_at, last_seen_at, event_count, screen_count)
VALUES(@platform, @session_id, @anonymous_id_hash, @started_at, @last_seen_at, @event_count, @screen_count)
ON CONFLICT(platform, session_id) DO UPDATE SET
    anonymous_id_hash = COALESCE(excluded.anonymous_id_hash, analytics_sessions.anonymous_id_hash),
    started_at        = MIN(analytics_sessions.started_at, excluded.started_at),
    last_seen_at      = MAX(analytics_sessions.last_seen_at, excluded.last_seen_at),
    event_count       = analytics_sessions.event_count  + excluded.event_count,
    screen_count      = analytics_sessions.screen_count + excluded.screen_count;";
                var platformParam   = cmd.Parameters.Add("@platform", SqliteType.Text);
                var sessionParam    = cmd.Parameters.Add("@session_id", SqliteType.Text);
                var anonParam       = cmd.Parameters.Add("@anonymous_id_hash", SqliteType.Text);
                var startedParam    = cmd.Parameters.Add("@started_at", SqliteType.Text);
                var lastSeenParam   = cmd.Parameters.Add("@last_seen_at", SqliteType.Text);
                var eventCountParam = cmd.Parameters.Add("@event_count", SqliteType.Integer);
                var screenCountParam= cmd.Parameters.Add("@screen_count", SqliteType.Integer);

                foreach (var d in tick.Sessions)
                {
                    platformParam.Value    = d.Platform;
                    sessionParam.Value     = d.SessionId;
                    anonParam.Value        = (object?)d.AnonymousIdHash ?? DBNull.Value;
                    startedParam.Value     = ToIso(d.StartedAt);
                    lastSeenParam.Value    = ToIso(d.LastSeenAt);
                    eventCountParam.Value  = d.EventCount;
                    screenCountParam.Value = d.ScreenCount;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            // 2) User-days: dedupe on (platform, day, hash) — the row is unique per user per day,
            //    but `events` accumulates. Replay-safe by virtue of the encompassing transaction.
            if (tick.UserDays.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
INSERT INTO analytics_user_days(platform, day, anonymous_id_hash, hash_version, events)
VALUES(@platform, @day, @anonymous_id_hash, @hash_version, @events)
ON CONFLICT(platform, day, anonymous_id_hash) DO UPDATE SET
    events       = analytics_user_days.events + excluded.events,
    hash_version = excluded.hash_version;";
                var platformParam = cmd.Parameters.Add("@platform", SqliteType.Text);
                var dayParam      = cmd.Parameters.Add("@day", SqliteType.Text);
                var anonParam     = cmd.Parameters.Add("@anonymous_id_hash", SqliteType.Text);
                var versionParam  = cmd.Parameters.Add("@hash_version", SqliteType.Integer);
                var eventsParam   = cmd.Parameters.Add("@events", SqliteType.Integer);

                foreach (var d in tick.UserDays)
                {
                    platformParam.Value = d.Platform;
                    dayParam.Value      = d.Day.ToString("yyyy-MM-dd");
                    anonParam.Value     = d.AnonymousIdHash;
                    versionParam.Value  = d.HashVersion;
                    eventsParam.Value   = d.Events;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            // 3) Daily rollups: events accumulate; sessions and distinct_users take MAX so a
            //    replay can't shrink them but also doesn't double-count.
            if (tick.DailyRollups.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
INSERT INTO analytics_daily_rollups(day, platform, events, sessions, distinct_users)
VALUES(@day, @platform, @events, @sessions, @distinct_users)
ON CONFLICT(day, platform) DO UPDATE SET
    events         = analytics_daily_rollups.events + excluded.events,
    sessions       = MAX(analytics_daily_rollups.sessions, excluded.sessions),
    distinct_users = MAX(analytics_daily_rollups.distinct_users, excluded.distinct_users);";
                var dayParam      = cmd.Parameters.Add("@day", SqliteType.Text);
                var platformParam = cmd.Parameters.Add("@platform", SqliteType.Text);
                var eventsParam   = cmd.Parameters.Add("@events", SqliteType.Integer);
                var sessionsParam = cmd.Parameters.Add("@sessions", SqliteType.Integer);
                var distinctParam = cmd.Parameters.Add("@distinct_users", SqliteType.Integer);

                foreach (var d in tick.DailyRollups)
                {
                    dayParam.Value      = d.Day.ToString("yyyy-MM-dd");
                    platformParam.Value = d.Platform;
                    eventsParam.Value   = d.Events;
                    sessionsParam.Value = d.Sessions;
                    distinctParam.Value = d.DistinctUsers;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            // 4) Mark events aggregated last. Same transaction means a crash anywhere above
            //    leaves these rows in the unaggregated pool, ready for the next tick to retry.
            if (tick.EventIds.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "UPDATE analytics_events SET aggregated_at = @ts WHERE event_id = @event_id;";
                var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);
                var idParam = cmd.Parameters.Add("@event_id", SqliteType.Text);
                tsParam.Value = nowIso;

                foreach (var id in tick.EventIds)
                {
                    idParam.Value = id;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------- Dashboards --------

    public async Task<RSCAnalyticsTotals> GetTotalsAsync(string? platform, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-6);
        var monthStart = today.AddDays(-29);

        var (clause, binder) = BuildPlatformClause(platform, "platform");
        var (sessionsClause, sessionsBinder) = BuildPlatformClause(platform, "platform");
        var todayStr = today.ToString("yyyy-MM-dd");

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            long dau, wau, mau;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = $@"
SELECT
  (SELECT COUNT(DISTINCT anonymous_id_hash) FROM analytics_user_days WHERE day = @today  {clause}),
  (SELECT COUNT(DISTINCT anonymous_id_hash) FROM analytics_user_days WHERE day >= @week  {clause}),
  (SELECT COUNT(DISTINCT anonymous_id_hash) FROM analytics_user_days WHERE day >= @month {clause});";
                cmd.Parameters.AddWithValue("@today", todayStr);
                cmd.Parameters.AddWithValue("@week", weekStart.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@month", monthStart.ToString("yyyy-MM-dd"));
                binder(cmd);
                using var r = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (await r.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    dau = r.GetInt64(0); wau = r.GetInt64(1); mau = r.GetInt64(2);
                }
                else
                {
                    dau = wau = mau = 0;
                }
            }

            long sessionsToday = 0, eventsToday = 0;
            double avgSessionSeconds = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = $@"
SELECT
  COUNT(*),
  COALESCE(SUM(event_count), 0),
  COALESCE(AVG((julianday(last_seen_at) - julianday(started_at)) * 86400.0), 0)
FROM analytics_sessions
WHERE started_at >= @start {sessionsClause};";
                cmd.Parameters.AddWithValue("@start", todayStr + "T00:00:00.0000000Z");
                sessionsBinder(cmd);
                using var r = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (await r.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    sessionsToday = r.GetInt64(0);
                    eventsToday = r.GetInt64(1);
                    avgSessionSeconds = r.GetDouble(2);
                }
            }

            DateTimeOffset? lastAggregated = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT MAX(aggregated_at) FROM analytics_events WHERE aggregated_at IS NOT NULL;";
                var raw = await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false);
                if (raw is string s && !string.IsNullOrEmpty(s)) lastAggregated = ParseIso(s);
            }

            return new RSCAnalyticsTotals(
                DailyActiveUsers: dau,
                WeeklyActiveUsers: wau,
                MonthlyActiveUsers: mau,
                SessionsToday: sessionsToday,
                EventsToday: eventsToday,
                AverageSessionDuration: TimeSpan.FromSeconds(Math.Max(0, avgSessionSeconds)),
                LastAggregatedAt: lastAggregated);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsPlatformSummary>> GetPlatformSummariesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT b.platform,
       COALESCE(SUM(b.accepted_count), 0),
       COALESCE(SUM(b.rejected_count), 0),
       COUNT(*),
       MAX(b.received_at)
FROM analytics_batches b
GROUP BY b.platform
ORDER BY b.platform;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsPlatformSummary>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;

            var result = new List<RSCAnalyticsPlatformSummary>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                DateTimeOffset? lastReceived = reader.IsDBNull(4) ? null : ParseIso(reader.GetString(4));
                result.Add(new RSCAnalyticsPlatformSummary(
                    Platform: reader.GetString(0),
                    AcceptedEvents: reader.GetInt64(1),
                    RejectedEvents: reader.GetInt64(2),
                    Batches: reader.GetInt64(3),
                    LastReceivedAt: lastReceived));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsTopScreen>> GetTopScreensAsync(string? platform, int topN, CancellationToken ct)
    {
        topN = Math.Clamp(topN, 1, 100);
        var (clause, binder) = BuildPlatformClause(platform, "platform");
        var sql = $@"
SELECT COALESCE(screen, '(unset)') AS k,
       COUNT(*) AS views,
       COALESCE(AVG(duration_ms), 0) AS avg_ms
FROM analytics_events
WHERE type = 'screen' {clause}
GROUP BY k
ORDER BY views DESC, k ASC
LIMIT @top;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsTopScreen>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@top", topN);
            binder(cmd);

            var result = new List<RSCAnalyticsTopScreen>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                var avgMs = reader.GetDouble(2);
                result.Add(new RSCAnalyticsTopScreen(
                    Screen: reader.GetString(0),
                    Views: reader.GetInt64(1),
                    AverageDuration: TimeSpan.FromMilliseconds(Math.Max(0, avgMs))));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsDailyRollup>> GetDailyRollupsAsync(
        DateTimeOffset from, DateTimeOffset until, string? platform, CancellationToken ct)
    {
        var (clause, binder) = BuildPlatformClause(platform, "platform");
        var sql = $@"
SELECT day, platform, events, sessions, distinct_users
FROM analytics_daily_rollups
WHERE day >= @from AND day < @until {clause}
ORDER BY day ASC, platform ASC;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsDailyRollup>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@from", DateOnly.FromDateTime(from.UtcDateTime).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@until", DateOnly.FromDateTime(until.UtcDateTime).ToString("yyyy-MM-dd"));
            binder(cmd);

            var result = new List<RSCAnalyticsDailyRollup>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                result.Add(new RSCAnalyticsDailyRollup(
                    Day: DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    Platform: reader.GetString(1),
                    Events: reader.GetInt64(2),
                    Sessions: reader.GetInt64(3),
                    DistinctUsers: reader.GetInt64(4)));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCAnalyticsHealthSnapshot> GetHealthSnapshotAsync(int sampleSize, CancellationToken ct)
    {
        sampleSize = Math.Clamp(sampleSize, 1, 100);

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            long total = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT COUNT(*) FROM analytics_dead_letters;";
                total = Convert.ToInt64(await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0L);
            }

            var byReason = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT reason, COUNT(*) FROM analytics_dead_letters GROUP BY reason;";
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    byReason[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            var samples = new List<RSCAnalyticsDeadLetterRow>(sampleSize);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT id, received_at, platform, batch_id, event_id, reason, detail, raw_json
FROM analytics_dead_letters
ORDER BY id DESC
LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", sampleSize);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    samples.Add(new RSCAnalyticsDeadLetterRow(
                        Id: reader.GetInt64(0),
                        ReceivedAt: ParseIso(reader.GetString(1)),
                        Platform: reader.GetString(2),
                        BatchId: reader.GetString(3),
                        EventId: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Reason: reader.GetString(5),
                        Detail: reader.IsDBNull(6) ? null : reader.GetString(6),
                        RawJson: reader.GetString(7)));
                }
            }

            var sdkVersions = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT sdk_version, COUNT(*) FROM analytics_batches
GROUP BY sdk_version
ORDER BY COUNT(*) DESC
LIMIT 20;";
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    sdkVersions[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            DateTimeOffset? lastAggregated = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT MAX(aggregated_at) FROM analytics_events WHERE aggregated_at IS NOT NULL;";
                var raw = await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false);
                if (raw is string s && !string.IsNullOrEmpty(s)) lastAggregated = ParseIso(s);
            }

            return new RSCAnalyticsHealthSnapshot(
                DeadLetterTotal: total,
                DeadLettersByReason: byReason,
                RecentSamples: samples,
                SdkVersionsSeen: sdkVersions,
                LastAggregatedAt: lastAggregated);
        }, ct).ConfigureAwait(false);
    }

    // -------- Search / detail --------

    public async Task<RSCAnalyticsEventPage> SearchEventsAsync(RSCAnalyticsEventFilter filter, CancellationToken ct)
    {
        var (whereSql, binder) = BuildEventWhere(filter);
        var limit = Math.Clamp(filter.Limit <= 0 ? 50 : filter.Limit, 1, 500);
        var offset = Math.Max(0, filter.Offset);

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            long total;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = $"SELECT COUNT(*) FROM analytics_events {whereSql};";
                binder(cmd);
                total = Convert.ToInt64(await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0L);
            }

            using var pageCmd = conn.CreateCommand();
            pageCmd.CommandTimeout = _commandTimeoutSeconds;
            pageCmd.CommandText = $@"
SELECT event_id, platform, session_id, anonymous_id_hash, sequence, occurred_at, type, name,
       screen, feature, duration_ms
FROM analytics_events
{whereSql}
ORDER BY occurred_at DESC
LIMIT @limit OFFSET @offset;";
            binder(pageCmd);
            pageCmd.Parameters.AddWithValue("@limit", limit);
            pageCmd.Parameters.AddWithValue("@offset", offset);

            var rows = new List<RSCAnalyticsStoredEvent>(limit);
            using var reader = await pageCmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(ReadStoredEvent(reader));
            }
            return new RSCAnalyticsEventPage(rows, total, limit, offset);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsStoredEvent>> GetSessionTimelineAsync(
        string platform, string sessionId, CancellationToken ct)
    {
        const string sql = @"
SELECT event_id, platform, session_id, anonymous_id_hash, sequence, occurred_at, type, name,
       screen, feature, duration_ms
FROM analytics_events
WHERE platform = @platform AND session_id = @session_id
ORDER BY sequence ASC, occurred_at ASC;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsStoredEvent>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@platform", platform.ToLowerInvariant());
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            var rows = new List<RSCAnalyticsStoredEvent>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(ReadStoredEvent(reader));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsSessionRow>> ListSessionsAsync(
        string? platform, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
        offset = Math.Max(0, offset);
        var (clause, binder) = BuildPlatformClause(platform, "platform");
        // Use the same placeholder convention but BuildPlatformClause prepends " AND" which is
        // fine inside the WHERE 1=1 form below.
        var sql = $@"
SELECT platform, session_id, anonymous_id_hash, started_at, last_seen_at, event_count, screen_count
FROM analytics_sessions
WHERE 1=1 {clause}
ORDER BY last_seen_at DESC
LIMIT @limit OFFSET @offset;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsSessionRow>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            binder(cmd);

            var rows = new List<RSCAnalyticsSessionRow>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCAnalyticsSessionRow(
                    Platform: reader.GetString(0),
                    SessionId: reader.GetString(1),
                    AnonymousIdHash: reader.IsDBNull(2) ? null : reader.GetString(2),
                    StartedAt: ParseIso(reader.GetString(3)),
                    LastSeenAt: ParseIso(reader.GetString(4)),
                    EventCount: reader.GetInt64(5),
                    ScreenCount: reader.GetInt64(6)));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    private static RSCAnalyticsStoredEvent ReadStoredEvent(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            EventId: reader.GetString(0),
            Platform: reader.GetString(1),
            SessionId: reader.GetString(2),
            AnonymousIdHash: reader.IsDBNull(3) ? null : reader.GetString(3),
            Sequence: reader.GetInt64(4),
            OccurredAt: ParseIso(reader.GetString(5)),
            Type: reader.GetString(6),
            Name: reader.GetString(7),
            Screen: reader.IsDBNull(8) ? null : reader.GetString(8),
            Feature: reader.IsDBNull(9) ? null : reader.GetString(9),
            DurationMs: reader.IsDBNull(10) ? null : reader.GetInt64(10));

    private static (string WhereSql, Action<SqliteCommand> Binder) BuildEventWhere(RSCAnalyticsEventFilter f)
    {
        var clauses = new List<string>();
        var binders = new List<Action<SqliteCommand>>();

        void Add(string clause, string name, object? value)
        {
            if (value is null) return;
            clauses.Add(clause);
            binders.Add(cmd => cmd.Parameters.AddWithValue(name, value));
        }

        Add("platform = @platform", "@platform", f.Platform?.ToLowerInvariant());
        Add("type = @type", "@type", f.Type);
        Add("name = @name", "@name", f.Name);
        Add("screen = @screen", "@screen", f.Screen);
        Add("session_id = @session_id", "@session_id", f.SessionId);
        if (f.From is { } from)
            Add("occurred_at >= @from", "@from", from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        if (f.Until is { } until)
            Add("occurred_at < @until", "@until", until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        return (where, cmd => { foreach (var b in binders) b(cmd); });
    }

    // -------- Retention cohorts --------

    public async Task<int> RecomputeRetentionCohortsAsync(
        DateOnly windowStart, int currentHashVersion, CancellationToken ct)
    {
        // Compute in-memory. The data set we read here is bounded by the (platform, day, hash)
        // tuples present in analytics_user_days inside the window — even at a few million users
        // per day this is well within a single SQLite scan.
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            // 1) First-seen day per (platform, hash) across all of history. We need history
            //    older than the window to know whether a user inside the window is genuinely
            //    "new" (first appearance ON install_day) or a returning user who just happened
            //    to be active in the window.
            var firstSeen = new Dictionary<(string Platform, string Hash), DateOnly>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT platform, anonymous_id_hash, MIN(day)
FROM analytics_user_days
WHERE hash_version = @hash_version
GROUP BY platform, anonymous_id_hash;";
                cmd.Parameters.AddWithValue("@hash_version", currentHashVersion);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    var platform = reader.GetString(0);
                    var hash = reader.GetString(1);
                    var day = DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
                    firstSeen[(platform, hash)] = day;
                }
            }

            // 2) All (platform, day, hash) observations inside the retention window — we look
            //    install_day +30 days into the future from windowStart, but to evaluate the D30
            //    retention of cohort install_day = windowStart, we need data up to today.
            var observed = new HashSet<(string Platform, DateOnly Day, string Hash)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT platform, day, anonymous_id_hash
FROM analytics_user_days
WHERE hash_version = @hash_version AND day >= @from;";
                cmd.Parameters.AddWithValue("@hash_version", currentHashVersion);
                cmd.Parameters.AddWithValue("@from", windowStart.ToString("yyyy-MM-dd"));
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    observed.Add((
                        reader.GetString(0),
                        DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                        reader.GetString(2)));
                }
            }

            // 3) Bucket first-seen users by (platform, install_day) within the window, then count
            //    retention checkpoints.
            var cohorts = new Dictionary<(string Platform, DateOnly InstallDay),
                (long Size, long D1, long D7, long D30)>();

            foreach (var ((platform, hash), installDay) in firstSeen)
            {
                if (installDay < windowStart) continue;

                var key = (platform, installDay);
                if (!cohorts.TryGetValue(key, out var counts))
                {
                    counts = (0, 0, 0, 0);
                }
                counts.Size++;
                if (observed.Contains((platform, installDay.AddDays(1),  hash))) counts.D1++;
                if (observed.Contains((platform, installDay.AddDays(7),  hash))) counts.D7++;
                if (observed.Contains((platform, installDay.AddDays(30), hash))) counts.D30++;
                cohorts[key] = counts;
            }

            // 4) Upsert each cohort. computed_at is set by the server clock so the page can
            //    show staleness.
            var computedAt = ToIso(DateTimeOffset.UtcNow);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
INSERT INTO analytics_retention_cohorts(
    platform, install_day, cohort_size, d1_retained, d7_retained, d30_retained,
    hash_version, computed_at)
VALUES(@platform, @install_day, @size, @d1, @d7, @d30, @hash_version, @computed_at)
ON CONFLICT(platform, install_day) DO UPDATE SET
    cohort_size  = excluded.cohort_size,
    d1_retained  = excluded.d1_retained,
    d7_retained  = excluded.d7_retained,
    d30_retained = excluded.d30_retained,
    hash_version = excluded.hash_version,
    computed_at  = excluded.computed_at;";
                var platformP   = cmd.Parameters.Add("@platform",     SqliteType.Text);
                var installP    = cmd.Parameters.Add("@install_day",  SqliteType.Text);
                var sizeP       = cmd.Parameters.Add("@size",         SqliteType.Integer);
                var d1P         = cmd.Parameters.Add("@d1",           SqliteType.Integer);
                var d7P         = cmd.Parameters.Add("@d7",           SqliteType.Integer);
                var d30P        = cmd.Parameters.Add("@d30",          SqliteType.Integer);
                var hvP         = cmd.Parameters.Add("@hash_version", SqliteType.Integer);
                var computedP   = cmd.Parameters.Add("@computed_at",  SqliteType.Text);
                hvP.Value       = currentHashVersion;
                computedP.Value = computedAt;

                foreach (var ((platform, installDay), counts) in cohorts)
                {
                    platformP.Value = platform;
                    installP.Value  = installDay.ToString("yyyy-MM-dd");
                    sizeP.Value     = counts.Size;
                    d1P.Value       = counts.D1;
                    d7P.Value       = counts.D7;
                    d30P.Value      = counts.D30;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);

            return cohorts.Count;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCAnalyticsRetentionSummary> GetRetentionSummaryAsync(
        string? platform, int windowDays, CancellationToken ct)
    {
        windowDays = Math.Clamp(windowDays, 7, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = today.AddDays(-windowDays);

        var (clause, binder) = BuildPlatformClause(platform, "platform");

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            // For each retention window we only count cohorts old enough to have observed it.
            // The day-string comparison works lexicographically because we always format as
            // yyyy-MM-dd; SQLite has no native DATE type but the format sorts correctly.
            cmd.CommandText = $@"
SELECT
  COALESCE(SUM(CASE WHEN install_day <= @d1cut  THEN d1_retained  ELSE 0 END), 0),
  COALESCE(SUM(CASE WHEN install_day <= @d1cut  THEN cohort_size  ELSE 0 END), 0),
  COALESCE(SUM(CASE WHEN install_day <= @d7cut  THEN d7_retained  ELSE 0 END), 0),
  COALESCE(SUM(CASE WHEN install_day <= @d7cut  THEN cohort_size  ELSE 0 END), 0),
  COALESCE(SUM(CASE WHEN install_day <= @d30cut THEN d30_retained ELSE 0 END), 0),
  COALESCE(SUM(CASE WHEN install_day <= @d30cut THEN cohort_size  ELSE 0 END), 0),
  COUNT(CASE WHEN install_day <= @d1cut  THEN 1 END),
  COUNT(CASE WHEN install_day <= @d7cut  THEN 1 END),
  COUNT(CASE WHEN install_day <= @d30cut THEN 1 END)
FROM analytics_retention_cohorts
WHERE install_day >= @from {clause};";
            cmd.Parameters.AddWithValue("@from",   windowStart.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@d1cut",  today.AddDays(-1).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@d7cut",  today.AddDays(-7).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@d30cut", today.AddDays(-30).ToString("yyyy-MM-dd"));
            binder(cmd);

            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            if (!await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                return new RSCAnalyticsRetentionSummary(0, 0, 0, 0, 0, 0);
            }

            double Ratio(long retained, long pool) => pool <= 0 ? 0 : (double)retained / pool;
            return new RSCAnalyticsRetentionSummary(
                Day1:  Ratio(reader.GetInt64(0), reader.GetInt64(1)),
                Day7:  Ratio(reader.GetInt64(2), reader.GetInt64(3)),
                Day30: Ratio(reader.GetInt64(4), reader.GetInt64(5)),
                CohortsUsedDay1:  reader.GetInt64(6),
                CohortsUsedDay7:  reader.GetInt64(7),
                CohortsUsedDay30: reader.GetInt64(8));
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsRetentionCohortRow>> ListRetentionCohortsAsync(
        string? platform, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = today.AddDays(-days);
        var (clause, binder) = BuildPlatformClause(platform, "platform");

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsRetentionCohortRow>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $@"
SELECT platform, install_day, cohort_size, d1_retained, d7_retained, d30_retained,
       hash_version, computed_at
FROM analytics_retention_cohorts
WHERE install_day >= @from {clause}
ORDER BY install_day DESC, platform ASC;";
            cmd.Parameters.AddWithValue("@from", windowStart.ToString("yyyy-MM-dd"));
            binder(cmd);

            var rows = new List<RSCAnalyticsRetentionCohortRow>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCAnalyticsRetentionCohortRow(
                    Platform: reader.GetString(0),
                    InstallDay: DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    CohortSize: reader.GetInt64(2),
                    Day1Retained: reader.GetInt64(3),
                    Day7Retained: reader.GetInt64(4),
                    Day30Retained: reader.GetInt64(5),
                    HashVersion: reader.GetInt32(6),
                    ComputedAt: ParseIso(reader.GetString(7))));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeUserDaysBelowHashVersionAsync(int minVersion, CancellationToken ct)
    {
        const string sql = "DELETE FROM analytics_user_days WHERE hash_version < @min;";
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@min", minVersion);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeOlderThanAsync(
        DateTimeOffset eventsCutoff, DateTimeOffset deadLetterCutoff, CancellationToken ct)
    {
        var eventsIso = ToIso(eventsCutoff);
        var dlqIso = ToIso(deadLetterCutoff);

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);

            int total = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "DELETE FROM analytics_events WHERE received_at < @cutoff;";
                cmd.Parameters.AddWithValue("@cutoff", eventsIso);
                total += await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "DELETE FROM analytics_dead_letters WHERE received_at < @cutoff;";
                cmd.Parameters.AddWithValue("@cutoff", dlqIso);
                total += await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);
            return total;
        }, ct).ConfigureAwait(false);
    }

    // -------- Funnels --------

    private static readonly System.Text.Json.JsonSerializerOptions FunnelStepsJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    public async Task<IReadOnlyList<RSCAnalyticsFunnelDefinition>> ListFunnelDefinitionsAsync(
        bool onlyEnabled, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsFunnelDefinition>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = onlyEnabled
                ? "SELECT funnel_key, display_name, steps_json, enabled, created_at, updated_at FROM analytics_funnel_definitions WHERE enabled = 1 ORDER BY funnel_key;"
                : "SELECT funnel_key, display_name, steps_json, enabled, created_at, updated_at FROM analytics_funnel_definitions ORDER BY funnel_key;";

            var rows = new List<RSCAnalyticsFunnelDefinition>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                var stepsJson = reader.GetString(2);
                var steps = System.Text.Json.JsonSerializer
                    .Deserialize<List<RSCAnalyticsFunnelStep>>(stepsJson, FunnelStepsJsonOptions)
                    ?? new List<RSCAnalyticsFunnelStep>();
                rows.Add(new RSCAnalyticsFunnelDefinition(
                    FunnelKey: reader.GetString(0),
                    DisplayName: reader.GetString(1),
                    Steps: steps,
                    Enabled: reader.GetInt64(3) != 0,
                    CreatedAt: ParseIso(reader.GetString(4)),
                    UpdatedAt: ParseIso(reader.GetString(5))));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task UpsertFunnelDefinitionAsync(RSCAnalyticsFunnelDefinition definition, CancellationToken ct)
    {
        var stepsJson = System.Text.Json.JsonSerializer.Serialize(definition.Steps, FunnelStepsJsonOptions);
        const string sql = @"
INSERT INTO analytics_funnel_definitions(funnel_key, display_name, steps_json, enabled, created_at, updated_at)
VALUES(@key, @name, @steps, @enabled, @created, @updated)
ON CONFLICT(funnel_key) DO UPDATE SET
  display_name = excluded.display_name,
  steps_json   = excluded.steps_json,
  enabled      = excluded.enabled,
  updated_at   = excluded.updated_at;";

        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@key", definition.FunnelKey);
            cmd.Parameters.AddWithValue("@name", definition.DisplayName);
            cmd.Parameters.AddWithValue("@steps", stepsJson);
            cmd.Parameters.AddWithValue("@enabled", definition.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@created", ToIso(definition.CreatedAt));
            cmd.Parameters.AddWithValue("@updated", ToIso(definition.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> RecomputeFunnelStepsAsync(
        RSCAnalyticsFunnelDefinition definition, DateOnly windowStart, CancellationToken ct)
    {
        if (definition.Steps.Count == 0) return 0;

        // Pull the (session_id, platform, sequence, type, name, occurred_at) tuples for events
        // in the window. Anything outside the window can't help — the same session won't have
        // a "reached step" boundary appearing inside the window unless its step events also
        // land inside the window (funnel_steps is INSERT OR IGNORE, so old observations stay).
        var windowIso = ToIso(new DateTimeOffset(windowStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            // session_id -> ordered list of (sequence, occurred_at, type, name, platform)
            var perSession = new Dictionary<string, List<(long Sequence, DateTimeOffset OccurredAt, string Type, string Name, string Platform)>>(StringComparer.Ordinal);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT session_id, platform, sequence, occurred_at, type, name
FROM analytics_events
WHERE occurred_at >= @from
ORDER BY session_id, sequence ASC, occurred_at ASC;";
                cmd.Parameters.AddWithValue("@from", windowIso);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    var sid = reader.GetString(0);
                    var platform = reader.GetString(1);
                    var seq = reader.GetInt64(2);
                    var at = ParseIso(reader.GetString(3));
                    var type = reader.GetString(4);
                    var name = reader.GetString(5);

                    if (!perSession.TryGetValue(sid, out var list))
                    {
                        list = new List<(long, DateTimeOffset, string, string, string)>();
                        perSession[sid] = list;
                    }
                    list.Add((seq, at, type, name, platform));
                }
            }

            // For each session, walk the events left-to-right and advance through the funnel's
            // step list as matches appear. Record (funnel_key, session_id, step_index) for each
            // reached step. INSERT OR IGNORE keeps the table idempotent across re-runs.
            var observations = new List<(int StepIndex, string Platform, string SessionId, DateTimeOffset ReachedAt)>();
            foreach (var (sessionId, events) in perSession)
            {
                int stepIdx = 0;
                foreach (var ev in events)
                {
                    if (stepIdx >= definition.Steps.Count) break;
                    var step = definition.Steps[stepIdx];
                    var typeMatches = step.EventType is null || string.Equals(ev.Type, step.EventType, StringComparison.Ordinal);
                    var nameMatches = string.Equals(ev.Name, step.EventName, StringComparison.Ordinal);
                    if (typeMatches && nameMatches)
                    {
                        observations.Add((stepIdx, ev.Platform, sessionId, ev.OccurredAt));
                        stepIdx++;
                    }
                }
            }

            if (observations.Count == 0) return 0;

            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
INSERT OR IGNORE INTO analytics_funnel_steps(funnel_key, step_index, platform, session_id, reached_at)
VALUES(@key, @idx, @platform, @session_id, @reached_at);";
                var keyP      = cmd.Parameters.Add("@key",        SqliteType.Text);
                var idxP      = cmd.Parameters.Add("@idx",        SqliteType.Integer);
                var platformP = cmd.Parameters.Add("@platform",   SqliteType.Text);
                var sessionP  = cmd.Parameters.Add("@session_id", SqliteType.Text);
                var reachedP  = cmd.Parameters.Add("@reached_at", SqliteType.Text);
                keyP.Value    = definition.FunnelKey;

                foreach (var obs in observations)
                {
                    idxP.Value      = obs.StepIndex;
                    platformP.Value = obs.Platform;
                    sessionP.Value  = obs.SessionId;
                    reachedP.Value  = ToIso(obs.ReachedAt);
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);

            return observations.Count;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsFunnelStepStat>> GetFunnelSummaryAsync(
        string funnelKey, DateTimeOffset from, DateTimeOffset until, string? platform, CancellationToken ct)
    {
        // The definition isn't queried here — the admin page calls this in parallel with the
        // definition fetch, and a step_index with no observations doesn't get a row. The caller
        // pads zeros against the known step list before rendering.
        var (clause, binder) = BuildPlatformClause(platform, "platform");

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsFunnelStepStat>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $@"
SELECT step_index, COUNT(DISTINCT session_id)
FROM analytics_funnel_steps
WHERE funnel_key = @key
  AND reached_at >= @from
  AND reached_at <  @until
  {clause}
GROUP BY step_index
ORDER BY step_index ASC;";
            cmd.Parameters.AddWithValue("@key", funnelKey);
            cmd.Parameters.AddWithValue("@from", ToIso(from));
            cmd.Parameters.AddWithValue("@until", ToIso(until));
            binder(cmd);

            var rows = new List<RSCAnalyticsFunnelStepStat>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCAnalyticsFunnelStepStat(
                    StepIndex: reader.GetInt32(0),
                    StepName: string.Empty, // populated by caller from the definition
                    SessionsReached: reader.GetInt64(1)));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    private static (string Clause, Action<SqliteCommand> Binder) BuildPlatformClause(string? platform, string column)
    {
        if (string.IsNullOrEmpty(platform)) return (string.Empty, _ => { });
        var lower = platform.ToLowerInvariant();
        return ($" AND {column} = @platform", cmd => cmd.Parameters.AddWithValue("@platform", lower));
    }
}
