using System.Globalization;
using Microsoft.Data.Sqlite;
using ReportService.Models;

namespace ReportService.Analytics;

public sealed partial class RSCSqliteAnalyticsStore
{
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
SELECT {StoredEventColumns}
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
        RSCAnalyticsScope scope, string sessionId, CancellationToken ct)
    {
        var (clause, binder) = BuildScopeClause(scope);
        var sql = $@"
SELECT {StoredEventColumns}
FROM analytics_events
WHERE session_id = @session_id {clause}
ORDER BY sequence ASC, occurred_at ASC;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsStoredEvent>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            binder(cmd);

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
        RSCAnalyticsScope scope, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
        offset = Math.Max(0, offset);
        var (clause, binder) = BuildScopeClause(scope);
        // BuildScopeClause prepends " AND" per component, which is fine inside the WHERE 1=1 form.
        var sql = $@"
SELECT app_id, environment, client_id, platform, session_id, anonymous_id_hash, started_at, last_seen_at, event_count, screen_count
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
                    AppId: reader.GetString(0),
                    Environment: reader.GetString(1),
                    ClientId: reader.GetString(2),
                    Platform: reader.GetString(3),
                    SessionId: reader.GetString(4),
                    AnonymousIdHash: reader.IsDBNull(5) ? null : reader.GetString(5),
                    StartedAt: ParseIso(reader.GetString(6)),
                    LastSeenAt: ParseIso(reader.GetString(7)),
                    EventCount: reader.GetInt64(8),
                    ScreenCount: reader.GetInt64(9)));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Canonical projection column order for <see cref="RSCAnalyticsStoredEvent"/>. Every
    /// SELECT feeding <see cref="ReadStoredEvent"/> must use this exact list so the ordinals line up.</summary>
    private const string StoredEventColumns =
        "app_id, environment, client_id, event_id, platform, session_id, anonymous_id_hash, sequence, occurred_at, type, name, screen, feature, duration_ms";

    private static RSCAnalyticsStoredEvent ReadStoredEvent(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            AppId: reader.GetString(0),
            Environment: reader.GetString(1),
            ClientId: reader.GetString(2),
            EventId: reader.GetString(3),
            Platform: reader.GetString(4),
            SessionId: reader.GetString(5),
            AnonymousIdHash: reader.IsDBNull(6) ? null : reader.GetString(6),
            Sequence: reader.GetInt64(7),
            OccurredAt: ParseIso(reader.GetString(8)),
            Type: reader.GetString(9),
            Name: reader.GetString(10),
            Screen: reader.IsDBNull(11) ? null : reader.GetString(11),
            Feature: reader.IsDBNull(12) ? null : reader.GetString(12),
            DurationMs: reader.IsDBNull(13) ? null : reader.GetInt64(13));

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

        Add("app_id = @app_id", "@app_id", f.AppId?.ToLowerInvariant());
        Add("environment = @environment", "@environment", f.Environment?.ToLowerInvariant());
        Add("client_id = @client_id", "@client_id", f.ClientId?.ToLowerInvariant());
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
}
