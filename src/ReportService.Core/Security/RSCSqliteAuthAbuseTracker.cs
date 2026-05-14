using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Security;

/// <summary>
/// Persisted <see cref="RSCIAuthAbuseTracker"/> backed by a dedicated SQLite file. State survives process
/// restarts so an attacker that bursts across restarts keeps their existing ban and failure window.
/// </summary>
/// <remarks>
/// Rows are keyed on <c>source</c> (typically a client IP). A row accumulates failures inside the
/// configured sliding window; older failures are silently dropped on every write. A <c>banned_until</c>
/// timestamp is set when the threshold is crossed and is cleared by a successful authentication or by
/// natural expiry.
/// </remarks>
public sealed class RSCSqliteAuthAbuseTracker : RSCIAuthAbuseTracker
{
    private readonly string _connectionString;
    private readonly RSCReportServiceOptions _options;
    private readonly ILogger<RSCSqliteAuthAbuseTracker> _logger;

    public RSCSqliteAuthAbuseTracker(RSCReportServiceOptions options, ILogger<RSCSqliteAuthAbuseTracker> logger)
    {
        _options = options;
        _logger = logger;

        // Anchor relative paths under the writable ReportsRoot — the process may be running with a
        // read-only content root / home, and we must not try to create a DB file there.
        var dbPath = RSCStatePaths.Resolve(options.AuthAbuseDbPath, options.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Bootstrap();
    }

    public async Task<AbuseDecision> CheckAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source)) return new AbuseDecision(false, 0);

        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = Math.Max(1, _options.SqliteCommandTimeoutSeconds);
        cmd.CommandText = "SELECT banned_until FROM auth_abuse WHERE source = @source;";
        cmd.Parameters.AddWithValue("@source", source);

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null || raw is DBNull) return new AbuseDecision(false, 0);

        var bannedUntil = DateTimeOffset.ParseExact(
            (string)raw, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        var now = DateTimeOffset.UtcNow;
        if (bannedUntil <= now)
        {
            // Natural expiry: best-effort clear so later checks are cheap. Don't fail the request
            // on a delete error.
            await TryClearAsync(source, ct).ConfigureAwait(false);
            return new AbuseDecision(false, 0);
        }

        var retryAfter = (int)Math.Ceiling((bannedUntil - now).TotalSeconds);
        return new AbuseDecision(true, Math.Max(1, retryAfter));
    }

    public async Task RecordFailureAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source)) return;

        var now = DateTimeOffset.UtcNow;
        var threshold = Math.Max(1, _options.AuthAbuseMaxFailures);
        var banDuration = Math.Max(1, _options.AuthAbuseBanSeconds);
        var windowStart = now.AddSeconds(-Math.Max(1, _options.AuthAbuseWindowSeconds));

        var nowIso = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var windowStartIso = windowStart.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var freshBanIso = now.AddSeconds(banDuration).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var extendedBanIso = freshBanIso; // same "now + banDuration" semantics either way

        // One atomic statement: upsert + compute new failures / window / banned_until in-place.
        // Two concurrent failures execute this once each; SQLite serialises them at the page level,
        // so the read-modify-write that used to drop updates is now a single UPDATE SET that always
        // reads the current row and commits in one step. RETURNING gives us the post-update state
        // for logging without a second SELECT.
        const string sql = @"
INSERT INTO auth_abuse(source, failures, window_started_at, banned_until)
VALUES(@source, 1, @now, NULL)
ON CONFLICT(source) DO UPDATE SET
  failures = CASE
    WHEN banned_until IS NOT NULL AND banned_until > @now THEN failures
    WHEN window_started_at < @window_start THEN 1
    ELSE failures + 1
  END,
  window_started_at = CASE
    WHEN banned_until IS NOT NULL AND banned_until > @now THEN window_started_at
    WHEN window_started_at < @window_start THEN @now
    ELSE window_started_at
  END,
  banned_until = CASE
    WHEN banned_until IS NOT NULL AND banned_until > @now THEN @extended_ban
    WHEN window_started_at < @window_start AND 1 >= @threshold THEN @fresh_ban
    WHEN window_started_at < @window_start THEN NULL
    WHEN (failures + 1) >= @threshold THEN @fresh_ban
    ELSE banned_until
  END
RETURNING failures, banned_until;";

        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = Math.Max(1, _options.SqliteCommandTimeoutSeconds);
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@now", nowIso);
        cmd.Parameters.AddWithValue("@window_start", windowStartIso);
        cmd.Parameters.AddWithValue("@threshold", threshold);
        cmd.Parameters.AddWithValue("@fresh_ban", freshBanIso);
        cmd.Parameters.AddWithValue("@extended_ban", extendedBanIso);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;

        var newFailures = reader.GetInt32(0);
        var newBanIso = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (newBanIso is not null && newFailures >= threshold)
        {
            _logger.LogWarning(
                "Auth abuse ban active for source={Source} (failures={Failures}; until {Until})",
                source, newFailures, newBanIso);
        }
    }

    public async Task ClearAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source)) return;
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = Math.Max(1, _options.SqliteCommandTimeoutSeconds);
        cmd.CommandText = "DELETE FROM auth_abuse WHERE source = @source;";
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task TryClearAsync(string source, CancellationToken ct)
    {
        try { await ClearAsync(source, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Auth abuse clear failed for {Source}", source); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
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

    private void Bootstrap()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS auth_abuse (
  source TEXT PRIMARY KEY,
  failures INTEGER NOT NULL,
  window_started_at TEXT NOT NULL,
  banned_until TEXT
);";
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Auth abuse tracker ready at {ConnectionString}", _connectionString);
    }
}
