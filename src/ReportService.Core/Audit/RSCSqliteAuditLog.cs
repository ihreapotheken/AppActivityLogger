using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Audit.Migrations;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Migrations;

namespace ReportService.Audit;

/// <summary>
/// SQLite-backed audit log. Bootstrap failures degrade to a no-op (the service keeps running,
/// a warning is logged); runtime write failures are swallowed so an unavailable audit DB never
/// blocks a destructive action from completing. Reads just return an empty list in that case.
/// </summary>
public sealed class RSCSqliteAuditLog : RSCIAuditLog
{
    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<RSCSqliteAuditLog> _logger;
    private readonly bool _ready;

    public RSCSqliteAuditLog(RSCReportServiceOptions options, ILogger<RSCSqliteAuditLog> logger)
    {
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, options.SqliteCommandTimeoutSeconds);

        var dbPath = RSCStatePaths.Resolve(options.AuditDbPath, options.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(parent))
        {
            try { Directory.CreateDirectory(parent); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit log directory unavailable; audit writes will degrade silently");
                _connectionString = string.Empty;
                return;
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }

            var runner = new RSCSchemaRunner(new RSCISchemaMigration[] { new RSCM001_CreateAuditLog() }, _logger);
            var version = runner.Run(conn);

            _ready = true;
            _logger.LogInformation("Audit log ready at {Path} (schema v{Version})", dbPath, version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit log bootstrap failed at {Path}; audit writes will degrade silently", dbPath);
        }
    }

    public async Task RecordAsync(RSCAuditEntry entry, CancellationToken ct)
    {
        if (!_ready) return;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
INSERT INTO audit_log(at, actor, remote, action, target, details, success)
VALUES(@at, @actor, @remote, @action, @target, @details, @success);";
            cmd.Parameters.AddWithValue("@at", entry.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@actor", entry.Actor);
            cmd.Parameters.AddWithValue("@remote", entry.RemoteAddress);
            cmd.Parameters.AddWithValue("@action", entry.Action);
            cmd.Parameters.AddWithValue("@target", (object?)entry.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@details", (object?)entry.Details ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit write failed for action={Action} actor={Actor}", entry.Action, entry.Actor);
        }
    }

    public async Task<IReadOnlyList<RSCAuditEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<RSCAuditEntry>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "SELECT at, actor, remote, action, target, details, success FROM audit_log ORDER BY id DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("@limit", Math.Max(1, Math.Min(limit, 500)));

            var rows = new List<RSCAuditEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new RSCAuditEntry(
                    At: DateTimeOffset.ParseExact(reader.GetString(0), "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Actor: reader.GetString(1),
                    RemoteAddress: reader.GetString(2),
                    Action: reader.GetString(3),
                    Target: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Details: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Success: reader.GetInt64(6) != 0));
            }
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit read failed");
            return Array.Empty<RSCAuditEntry>();
        }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_log;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }
        catch
        {
            return 0;
        }
    }
}
