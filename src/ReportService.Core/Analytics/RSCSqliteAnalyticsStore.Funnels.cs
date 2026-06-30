using System.Globalization;
using Microsoft.Data.Sqlite;
using ReportService.Models;

namespace ReportService.Analytics;

public sealed partial class RSCSqliteAnalyticsStore
{
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

        // Pull only the events whose name matches one of this funnel's steps, in the window.
        // Anything outside the window can't help — the same session won't have a "reached step"
        // boundary appearing inside the window unless its step events also land inside the window
        // (funnel_steps is INSERT OR IGNORE, so old observations stay). Filtering by step names in
        // SQL keeps the in-memory set proportional to candidate events, not the whole 14-day event
        // pool: an event whose name matches no step can never advance the matcher, so dropping it
        // server-side is behavior-identical to scanning everything and skipping it in the walk.
        var windowIso = ToIso(new DateTimeOffset(windowStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        // Distinct step event names — these are the only `name` values the matcher can ever accept.
        var stepNames = definition.Steps
            .Select(s => s.EventName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (stepNames.Count == 0) return 0;

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            // (app, env, client, platform, session_id) -> ordered list of (sequence, occurred_at,
            // type, name). The key includes the full tenant + platform: session ids are
            // caller-supplied and can collide across tenants/platforms, so each must walk
            // independently or their events interleave and corrupt step ordering.
            var perSession = new Dictionary<(string App, string Env, string Client, string Platform, string SessionId),
                List<(long Sequence, DateTimeOffset OccurredAt, string Type, string Name)>>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                var nameParams = new List<string>(stepNames.Count);
                for (var i = 0; i < stepNames.Count; i++)
                {
                    var p = "@name" + i.ToString(CultureInfo.InvariantCulture);
                    nameParams.Add(p);
                    cmd.Parameters.AddWithValue(p, stepNames[i]);
                }
                cmd.CommandText = $@"
SELECT app_id, environment, client_id, session_id, platform, sequence, occurred_at, type, name
FROM analytics_events
WHERE occurred_at >= @from AND name IN ({string.Join(", ", nameParams)})
ORDER BY app_id, environment, client_id, platform, session_id, sequence ASC, occurred_at ASC;";
                cmd.Parameters.AddWithValue("@from", windowIso);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    var app = reader.GetString(0);
                    var env = reader.GetString(1);
                    var client = reader.GetString(2);
                    var sid = reader.GetString(3);
                    var platform = reader.GetString(4);
                    var seq = reader.GetInt64(5);
                    var at = ParseIso(reader.GetString(6));
                    var type = reader.GetString(7);
                    var name = reader.GetString(8);

                    var key = (app, env, client, platform, sid);
                    if (!perSession.TryGetValue(key, out var list))
                    {
                        list = new List<(long, DateTimeOffset, string, string)>();
                        perSession[key] = list;
                    }
                    list.Add((seq, at, type, name));
                }
            }

            // For each (app, env, client, platform, session) walk events left-to-right and advance
            // through the funnel's step list as matches appear. INSERT OR IGNORE keeps it idempotent.
            var observations = new List<(int StepIndex, string App, string Env, string Client, string Platform, string SessionId, DateTimeOffset ReachedAt)>();
            foreach (var ((app, env, client, platform, sessionId), events) in perSession)
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
                        observations.Add((stepIdx, app, env, client, platform, sessionId, ev.OccurredAt));
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
INSERT OR IGNORE INTO analytics_funnel_steps(funnel_key, step_index, app_id, environment, client_id, platform, session_id, reached_at)
VALUES(@key, @idx, @app_id, @environment, @client_id, @platform, @session_id, @reached_at);";
                var keyP      = cmd.Parameters.Add("@key",        SqliteType.Text);
                var idxP      = cmd.Parameters.Add("@idx",        SqliteType.Integer);
                var appP      = cmd.Parameters.Add("@app_id",     SqliteType.Text);
                var envP      = cmd.Parameters.Add("@environment", SqliteType.Text);
                var clientP   = cmd.Parameters.Add("@client_id",  SqliteType.Text);
                var platformP = cmd.Parameters.Add("@platform",   SqliteType.Text);
                var sessionP  = cmd.Parameters.Add("@session_id", SqliteType.Text);
                var reachedP  = cmd.Parameters.Add("@reached_at", SqliteType.Text);
                keyP.Value    = definition.FunnelKey;

                foreach (var obs in observations)
                {
                    idxP.Value      = obs.StepIndex;
                    appP.Value      = obs.App;
                    envP.Value      = obs.Env;
                    clientP.Value   = obs.Client;
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
        string funnelKey, DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, CancellationToken ct)
    {
        // The definition isn't queried here — the admin page calls this in parallel with the
        // definition fetch, and a step_index with no observations doesn't get a row. The caller
        // pads zeros against the known step list before rendering.
        var (clause, binder) = BuildScopeClause(scope);

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
}
