using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Models;

namespace ReportService.Analytics;

public sealed partial class RSCSqliteAnalyticsStore
{
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

            // 1) First-seen day per (app, env, client, platform, hash) across all of history. We need
            //    history older than the window to know whether a user inside the window is genuinely
            //    "new" (first appearance ON install_day) or a returning user who just happened
            //    to be active in the window.
            var firstSeen = new Dictionary<(string App, string Env, string Client, string Platform, string Hash), DateOnly>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT app_id, environment, client_id, platform, anonymous_id_hash, MIN(day)
FROM analytics_user_days
WHERE hash_version = @hash_version
GROUP BY app_id, environment, client_id, platform, anonymous_id_hash;";
                cmd.Parameters.AddWithValue("@hash_version", currentHashVersion);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    var day = DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
                    firstSeen[(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))] = day;
                }
            }

            // 1b) Rotation-boundary detection. After a pepper/hash_version rotation a long-tenured
            //     user's pre-rotation rows carry the OLD hash+version while their post-rotation rows
            //     carry a brand-new hash — the two identities cannot be bridged (raw IDs were never
            //     stored). The MIN(day) above therefore makes every still-active user look like a
            //     fresh install on the first day the new version appears, producing a spurious
            //     "new install" spike. We can't recover the true install day across the boundary,
            //     so for each platform whose current-version history is preceded by older-version
            //     activity we mark that first current-version day as a rotation boundary and skip
            //     its install cohort rather than record a misleading one. (Operators clear the old
            //     rows via PurgeUserDaysBelowHashVersionAsync; once gone, no boundary is detected.)
            var rotationBoundary = new Dictionary<(string App, string Env, string Client, string Platform), DateOnly>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT cur.app_id, cur.environment, cur.client_id, cur.platform, cur.first_day
FROM (
    SELECT app_id, environment, client_id, platform, MIN(day) AS first_day
    FROM analytics_user_days
    WHERE hash_version = @hash_version
    GROUP BY app_id, environment, client_id, platform
) cur
WHERE EXISTS (
    SELECT 1 FROM analytics_user_days old
    WHERE old.app_id = cur.app_id AND old.environment = cur.environment
      AND old.client_id = cur.client_id AND old.platform = cur.platform
      AND old.hash_version < @hash_version
      AND old.day < cur.first_day
);";
                cmd.Parameters.AddWithValue("@hash_version", currentHashVersion);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    rotationBoundary[(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))] =
                        DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
                }
            }

            // 2) All (platform, day, hash) observations inside the retention window — we look
            //    install_day +30 days into the future from windowStart, but to evaluate the D30
            //    retention of cohort install_day = windowStart, we need data up to today.
            var observed = new HashSet<(string App, string Env, string Client, string Platform, DateOnly Day, string Hash)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT app_id, environment, client_id, platform, day, anonymous_id_hash
FROM analytics_user_days
WHERE hash_version = @hash_version AND day >= @from;";
                cmd.Parameters.AddWithValue("@hash_version", currentHashVersion);
                cmd.Parameters.AddWithValue("@from", windowStart.ToString("yyyy-MM-dd"));
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    observed.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                        reader.GetString(5)));
                }
            }

            // 3) Bucket first-seen users by (app, env, client, platform, install_day) within the
            //    window, then count retention checkpoints.
            var cohorts = new Dictionary<(string App, string Env, string Client, string Platform, DateOnly InstallDay),
                (long Size, long D1, long D7, long D30)>();

            var suppressedBoundaryUsers = 0L;
            foreach (var ((app, env, client, platform, hash), installDay) in firstSeen)
            {
                if (installDay < windowStart) continue;

                // Skip the rotation-boundary cohort: a "first seen" on the day the current
                // hash_version begins (when older-version history precedes it) is a population
                // rollover, not a real install, so it would overcount new users.
                if (rotationBoundary.TryGetValue((app, env, client, platform), out var boundary) && installDay == boundary)
                {
                    suppressedBoundaryUsers++;
                    continue;
                }

                var key = (app, env, client, platform, installDay);
                if (!cohorts.TryGetValue(key, out var counts))
                {
                    counts = (0, 0, 0, 0);
                }
                counts.Size++;
                if (observed.Contains((app, env, client, platform, installDay.AddDays(1),  hash))) counts.D1++;
                if (observed.Contains((app, env, client, platform, installDay.AddDays(7),  hash))) counts.D7++;
                if (observed.Contains((app, env, client, platform, installDay.AddDays(30), hash))) counts.D30++;
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
    app_id, environment, client_id, platform, install_day, cohort_size, d1_retained, d7_retained, d30_retained,
    hash_version, computed_at)
VALUES(@app_id, @environment, @client_id, @platform, @install_day, @size, @d1, @d7, @d30, @hash_version, @computed_at)
ON CONFLICT(app_id, environment, client_id, platform, install_day) DO UPDATE SET
    cohort_size  = excluded.cohort_size,
    d1_retained  = excluded.d1_retained,
    d7_retained  = excluded.d7_retained,
    d30_retained = excluded.d30_retained,
    hash_version = excluded.hash_version,
    computed_at  = excluded.computed_at;";
                var appP        = cmd.Parameters.Add("@app_id",       SqliteType.Text);
                var envP        = cmd.Parameters.Add("@environment",  SqliteType.Text);
                var clientP     = cmd.Parameters.Add("@client_id",    SqliteType.Text);
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

                foreach (var ((app, env, client, platform, installDay), counts) in cohorts)
                {
                    appP.Value      = app;
                    envP.Value      = env;
                    clientP.Value   = client;
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

            if (suppressedBoundaryUsers > 0)
            {
                _logger.LogWarning(
                    "Retention cohorts: suppressed {Users} users on {Platforms} rotation-boundary day(s) " +
                    "(hash_version {Version}) — cohorts spanning a pepper rotation are unreliable until " +
                    "older hash_version rows are purged.",
                    suppressedBoundaryUsers, rotationBoundary.Count, currentHashVersion);
            }

            return cohorts.Count;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCAnalyticsRetentionSummary> GetRetentionSummaryAsync(
        RSCAnalyticsScope scope, int windowDays, CancellationToken ct)
    {
        windowDays = Math.Clamp(windowDays, 7, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = today.AddDays(-windowDays);

        var (clause, binder) = BuildScopeClause(scope);

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
        RSCAnalyticsScope scope, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = today.AddDays(-days);
        var (clause, binder) = BuildScopeClause(scope);

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsRetentionCohortRow>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $@"
SELECT app_id, environment, client_id, platform, install_day, cohort_size, d1_retained, d7_retained, d30_retained,
       hash_version, computed_at
FROM analytics_retention_cohorts
WHERE install_day >= @from {clause}
ORDER BY install_day DESC, app_id ASC, environment ASC, client_id ASC, platform ASC;";
            cmd.Parameters.AddWithValue("@from", windowStart.ToString("yyyy-MM-dd"));
            binder(cmd);

            var rows = new List<RSCAnalyticsRetentionCohortRow>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCAnalyticsRetentionCohortRow(
                    AppId: reader.GetString(0),
                    Environment: reader.GetString(1),
                    ClientId: reader.GetString(2),
                    Platform: reader.GetString(3),
                    InstallDay: DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    CohortSize: reader.GetInt64(5),
                    Day1Retained: reader.GetInt64(6),
                    Day7Retained: reader.GetInt64(7),
                    Day30Retained: reader.GetInt64(8),
                    HashVersion: reader.GetInt32(9),
                    ComputedAt: ParseIso(reader.GetString(10))));
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
                // Only trim raw events the aggregation worker has already folded. Without the
                // aggregated_at guard, a backed-up/crash-looping aggregator lets the purge delete
                // rows past the cutoff before they ever reach a rollup — permanent, silent undercount.
                cmd.CommandText = "DELETE FROM analytics_events WHERE received_at < @cutoff AND aggregated_at IS NOT NULL;";
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

    // Per-app store: this IS one app's database, so the scope's client/app routing was already applied
    // by the fan-out; the scope is accepted only to satisfy the interface. A wipe is always total here.
    public async Task<int> WipeAllDataAsync(RSCAnalyticsScope scope, CancellationToken ct)
    {
        _ = scope;
        // Every analytics DATA table. analytics_funnel_definitions is operator config and is kept
        // (the tenancy catalog + audit log survive a report wipe for the same reason). The names are
        // compile-time literals — no injection surface. No cross-table FKs, so order is irrelevant.
        string[] tables =
        {
            "analytics_events", "analytics_sessions", "analytics_user_days", "analytics_daily_rollups",
            "analytics_funnel_steps", "analytics_retention_cohorts", "analytics_batches", "analytics_dead_letters",
        };
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
            int total = 0;
            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = $"DELETE FROM {table};";
                total += await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);
            return total;
        }, ct).ConfigureAwait(false);
    }
}
