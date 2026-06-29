using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.Storage;

/// <summary>
/// SQLite-backed allow-list of identifiers that the mobile app should treat as "forced report"
/// triggers. Shares <c>reports.db</c> with the problem-report index so a single backup file
/// captures both. The table is owned by <see cref="Migrations.Reports.RSCM004_CreateForcedReports"/>;
/// this class additionally calls a defensive <c>CREATE TABLE IF NOT EXISTS</c> at construction so
/// it works even if instantiated before <see cref="RSCSqliteReportIndex"/> has bootstrapped.
/// </summary>
public sealed class RSCSqliteForcedReportStore : RSCIForcedReportStore
{
    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    // See RSCSqliteReportIndex: shared cache turns DB-level SQLITE_BUSY into table-level
    // SQLITE_LOCKED, which busy_timeout does NOT cover — hence the retry helper below as well.
    private const int BusyTimeoutMs = 5_000;

    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<RSCSqliteForcedReportStore> _logger;

    public RSCSqliteForcedReportStore(RSCReportServiceOptions options, ILogger<RSCSqliteForcedReportStore> logger)
    {
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, options.SqliteCommandTimeoutSeconds);
        var dbPath = RSCStatePaths.Resolve(options.SqliteDbPath, options.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        EnsureTable();
    }

    public async Task<bool> ContainsAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return false;

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "SELECT 1 FROM forced_reports WHERE id = @id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", id);
            var found = await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false);
            return found is not null;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> AddAsync(string id, string? note, CancellationToken ct)
    {
        // INSERT OR REPLACE re-stamps addedAt and overwrites the note. We return whether a NEW
        // row was inserted (vs. an existing one updated) so the admin UI can show "added" / "updated"
        // toasts accurately. Detection: SELECT before, then INSERT OR REPLACE.
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);

            bool wasPresent;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandTimeout = _commandTimeoutSeconds;
                probe.CommandText = "SELECT 1 FROM forced_reports WHERE id = @id LIMIT 1;";
                probe.Parameters.AddWithValue("@id", id);
                wasPresent = (await probe.ExecuteScalarAsync(innerCt).ConfigureAwait(false)) is not null;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
INSERT INTO forced_reports(id, added_at, note) VALUES(@id, @added_at, @note)
ON CONFLICT(id) DO UPDATE SET added_at = excluded.added_at, note = excluded.note;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@added_at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            return !wasPresent;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "DELETE FROM forced_reports WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            var n = await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            return n > 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCForcedReportEntry>> ListAsync(CancellationToken ct)
    {
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCForcedReportEntry>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "SELECT id, added_at, note FROM forced_reports ORDER BY added_at DESC;";

            var rows = new List<RSCForcedReportEntry>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var addedAt = ParseFlexibleTimestamp(reader.GetString(1));
                var note = reader.IsDBNull(2) ? null : reader.GetString(2);
                rows.Add(new RSCForcedReportEntry(id, addedAt, note));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tolerant ISO-8601 parser. The store always writes with the round-trip "O" format (full
    /// fractional-seconds precision), but the table is operator-managed — rows added by hand
    /// via <c>sqlite3</c> commonly come in as <c>2026-05-06T18:10:39Z</c> with no fractional
    /// part. Strict <c>ParseExact("O", …)</c> rejects those and the page crashes; this parser
    /// accepts either shape and falls back to <c>UtcNow</c> on garbage so the listing degrades
    /// rather than throws.
    /// </summary>
    private static DateTimeOffset ParseFlexibleTimestamp(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return DateTimeOffset.UtcNow;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EnsureTable()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
            pragma.ExecuteNonQuery();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS forced_reports (
  id        TEXT PRIMARY KEY,
  added_at  TEXT NOT NULL,
  note      TEXT NULL
);";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to bootstrap forced_reports table");
            throw;
        }
    }

    // Retry with backoff for SQLITE_BUSY / SQLITE_LOCKED — mirrors RSCSqliteReportIndex's helper so
    // shared-cache table locks on reports.db don't surface straight to callers. Non-busy SQLite
    // errors are logged and rethrown.
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
                _logger.LogWarning(
                    ex,
                    "Forced-report store SQLite busy on attempt {Attempt}/{Max}; retrying in {DelayMs}ms",
                    attempt, MaxBusyRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex, "Forced-report store SQLite operation failed (attempt {Attempt})", attempt);
                throw;
            }
        }
    }

    private static bool IsBusy(SqliteException ex)
        => ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;
}
