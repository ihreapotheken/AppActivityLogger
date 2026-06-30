using Microsoft.Data.Sqlite;
using ReportService.Models;

namespace ReportService.Analytics;

public sealed partial class RSCSqliteAnalyticsStore
{
    // -------- Aggregation --------

    public async Task<IReadOnlyList<RSCAnalyticsStoredEvent>> ListUnaggregatedEventsAsync(
        int limit, CancellationToken ct)
    {
        if (limit <= 0) return Array.Empty<RSCAnalyticsStoredEvent>();

        var sql = $@"
SELECT {StoredEventColumns}
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
                result.Add(ReadStoredEvent(reader));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task MarkEventsAggregatedAsync(IReadOnlyList<RSCAggregationEventRef> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = _commandTimeoutSeconds;
            // Match the full UNIQUE(app_id, environment, client_id, platform, event_id) key — marking
            // by fewer columns would mis-mark a same-id row in another tenant/platform (incl. the
            // backend server path).
            cmd.CommandText = @"
UPDATE analytics_events SET aggregated_at = @ts
WHERE app_id = @app_id AND environment = @environment AND client_id = @client_id
  AND platform = @platform AND event_id = @event_id;";
            var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);
            var appParam = cmd.Parameters.Add("@app_id", SqliteType.Text);
            var envParam = cmd.Parameters.Add("@environment", SqliteType.Text);
            var clientParam = cmd.Parameters.Add("@client_id", SqliteType.Text);
            var platformParam = cmd.Parameters.Add("@platform", SqliteType.Text);
            var idParam = cmd.Parameters.Add("@event_id", SqliteType.Text);

            tsParam.Value = ToIso(DateTimeOffset.UtcNow);
            foreach (var e in events)
            {
                appParam.Value = e.AppId;
                envParam.Value = e.Environment;
                clientParam.Value = e.ClientId;
                platformParam.Value = e.Platform;
                idParam.Value = e.EventId;
                await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task WriteAggregationTickAsync(RSCAnalyticsAggregationTick tick, CancellationToken ct)
    {
        if (tick.Events.Count == 0 &&
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
INSERT INTO analytics_sessions(app_id, environment, client_id, platform, session_id, anonymous_id_hash, started_at, last_seen_at, event_count, screen_count)
VALUES(@app_id, @environment, @client_id, @platform, @session_id, @anonymous_id_hash, @started_at, @last_seen_at, @event_count, @screen_count)
ON CONFLICT(app_id, environment, client_id, platform, session_id) DO UPDATE SET
    anonymous_id_hash = COALESCE(excluded.anonymous_id_hash, analytics_sessions.anonymous_id_hash),
    started_at        = MIN(analytics_sessions.started_at, excluded.started_at),
    last_seen_at      = MAX(analytics_sessions.last_seen_at, excluded.last_seen_at),
    event_count       = analytics_sessions.event_count  + excluded.event_count,
    screen_count      = analytics_sessions.screen_count + excluded.screen_count;";
                var appParam        = cmd.Parameters.Add("@app_id", SqliteType.Text);
                var envParam        = cmd.Parameters.Add("@environment", SqliteType.Text);
                var clientParam     = cmd.Parameters.Add("@client_id", SqliteType.Text);
                var platformParam   = cmd.Parameters.Add("@platform", SqliteType.Text);
                var sessionParam    = cmd.Parameters.Add("@session_id", SqliteType.Text);
                var anonParam       = cmd.Parameters.Add("@anonymous_id_hash", SqliteType.Text);
                var startedParam    = cmd.Parameters.Add("@started_at", SqliteType.Text);
                var lastSeenParam   = cmd.Parameters.Add("@last_seen_at", SqliteType.Text);
                var eventCountParam = cmd.Parameters.Add("@event_count", SqliteType.Integer);
                var screenCountParam= cmd.Parameters.Add("@screen_count", SqliteType.Integer);

                foreach (var d in tick.Sessions)
                {
                    appParam.Value         = d.AppId;
                    envParam.Value         = d.Environment;
                    clientParam.Value      = d.ClientId;
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
INSERT INTO analytics_user_days(app_id, environment, client_id, platform, day, anonymous_id_hash, hash_version, events)
VALUES(@app_id, @environment, @client_id, @platform, @day, @anonymous_id_hash, @hash_version, @events)
ON CONFLICT(app_id, environment, client_id, platform, day, anonymous_id_hash) DO UPDATE SET
    events       = analytics_user_days.events + excluded.events,
    hash_version = excluded.hash_version;";
                var appParam      = cmd.Parameters.Add("@app_id", SqliteType.Text);
                var envParam      = cmd.Parameters.Add("@environment", SqliteType.Text);
                var clientParam   = cmd.Parameters.Add("@client_id", SqliteType.Text);
                var platformParam = cmd.Parameters.Add("@platform", SqliteType.Text);
                var dayParam      = cmd.Parameters.Add("@day", SqliteType.Text);
                var anonParam     = cmd.Parameters.Add("@anonymous_id_hash", SqliteType.Text);
                var versionParam  = cmd.Parameters.Add("@hash_version", SqliteType.Integer);
                var eventsParam   = cmd.Parameters.Add("@events", SqliteType.Integer);

                foreach (var d in tick.UserDays)
                {
                    appParam.Value      = d.AppId;
                    envParam.Value      = d.Environment;
                    clientParam.Value   = d.ClientId;
                    platformParam.Value = d.Platform;
                    dayParam.Value      = d.Day.ToString("yyyy-MM-dd");
                    anonParam.Value     = d.AnonymousIdHash;
                    versionParam.Value  = d.HashVersion;
                    eventsParam.Value   = d.Events;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            // 3) Daily rollups: `events` accumulates across ticks (each tick's bounded batch is a
            //    disjoint slice of the day). `sessions` and `distinct_users` CANNOT be summed
            //    (cross-tick repeats double-count) nor MAXed (a day spread over many ticks reports
            //    only the largest single tick) from per-tick deltas. Recompute them authoritatively
            //    from analytics_sessions / analytics_user_days for that (platform, day) — both were
            //    already upserted above in this same transaction, so the counts are current.
            if (tick.DailyRollups.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                // The two COUNT subqueries MUST carry the full tenancy predicate or they'd recompute
                // sessions / distinct_users across every tenant sharing the (day, platform) and write
                // inflated per-tenant rollups.
                cmd.CommandText = @"
INSERT INTO analytics_daily_rollups(app_id, environment, client_id, day, platform, events, sessions, distinct_users)
VALUES(
    @app_id, @environment, @client_id, @day, @platform, @events,
    (SELECT COUNT(*) FROM analytics_sessions  WHERE app_id = @app_id AND environment = @environment AND client_id = @client_id AND platform = @platform AND date(started_at) = @day),
    (SELECT COUNT(*) FROM analytics_user_days  WHERE app_id = @app_id AND environment = @environment AND client_id = @client_id AND platform = @platform AND day = @day))
ON CONFLICT(app_id, environment, client_id, day, platform) DO UPDATE SET
    events         = analytics_daily_rollups.events + excluded.events,
    sessions       = excluded.sessions,
    distinct_users = excluded.distinct_users;";
                var appParam      = cmd.Parameters.Add("@app_id", SqliteType.Text);
                var envParam      = cmd.Parameters.Add("@environment", SqliteType.Text);
                var clientParam   = cmd.Parameters.Add("@client_id", SqliteType.Text);
                var dayParam      = cmd.Parameters.Add("@day", SqliteType.Text);
                var platformParam = cmd.Parameters.Add("@platform", SqliteType.Text);
                var eventsParam   = cmd.Parameters.Add("@events", SqliteType.Integer);

                foreach (var d in tick.DailyRollups)
                {
                    appParam.Value      = d.AppId;
                    envParam.Value      = d.Environment;
                    clientParam.Value   = d.ClientId;
                    dayParam.Value      = d.Day.ToString("yyyy-MM-dd");
                    platformParam.Value = d.Platform;
                    eventsParam.Value   = d.Events;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            // 4) Mark events aggregated last. Same transaction means a crash anywhere above
            //    leaves these rows in the unaggregated pool, ready for the next tick to retry.
            //    Match the UNIQUE(platform, event_id) key on BOTH columns: marking by event_id
            //    alone would also stamp a same-id row on another platform that this tick never
            //    folded, silently dropping it from every rollup.
            if (tick.Events.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
UPDATE analytics_events SET aggregated_at = @ts
WHERE app_id = @app_id AND environment = @environment AND client_id = @client_id
  AND platform = @platform AND event_id = @event_id;";
                var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);
                var appParam = cmd.Parameters.Add("@app_id", SqliteType.Text);
                var envParam = cmd.Parameters.Add("@environment", SqliteType.Text);
                var clientParam = cmd.Parameters.Add("@client_id", SqliteType.Text);
                var platformParam = cmd.Parameters.Add("@platform", SqliteType.Text);
                var idParam = cmd.Parameters.Add("@event_id", SqliteType.Text);
                tsParam.Value = nowIso;

                foreach (var e in tick.Events)
                {
                    appParam.Value = e.AppId;
                    envParam.Value = e.Environment;
                    clientParam.Value = e.ClientId;
                    platformParam.Value = e.Platform;
                    idParam.Value = e.EventId;
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
