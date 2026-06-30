using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Migrations;
using ReportService.Storage.Migrations.Reports;

namespace ReportService.Storage;

/// <summary>
/// <see cref="RSCIReportIndex"/> implementation backed by a local SQLite database. The database stores
/// only metadata about persisted problem reports; the canonical JSON and gzip bytes continue to live
/// on disk under <see cref="RSCReportServiceOptions.ReportsRoot"/>.
/// </summary>
/// <remarks>
/// The connection string is derived from <see cref="RSCReportServiceOptions.SqliteDbPath"/>, which is a
/// trusted admin-supplied path. It is not path-traversal validated here — treat it as server configuration.
/// All statements are parameterised via <see cref="SqliteCommand.Parameters"/>.
/// </remarks>
public sealed class RSCSqliteReportIndex : RSCIReportIndex, RSCIReportIndexMaintenance
{
    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    // busy_timeout lets SQLite block-and-retry internally on SQLITE_BUSY (DB-level locks) before
    // surfacing an error, complementing the hand-rolled ExecuteWithRetryAsync (which is the only
    // mitigation for table-level SQLITE_LOCKED introduced by shared-cache mode).
    private const int BusyTimeoutMs = 5_000;

    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly string _backupRoot;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<RSCSqliteReportIndex> _logger;
    private int _schemaVersion;

    // Guards the mutable maintenance/status fields below. The index is a singleton hit concurrently
    // by ingestion, the admin status page, and maintenance actions, so writes (IntegrityCheckAsync /
    // BackupAsync / RebuildAsync) and the GetStatusAsync read must not observe torn combinations
    // (e.g. a fresh _lastBackupAt paired with a stale _lastBackupPath).
    private readonly object _statusLock = new();
    private string? _lastIntegrityResult;
    private DateTimeOffset? _lastIntegrityAt;
    private DateTimeOffset? _lastBackupAt;
    private string? _lastBackupPath;
    private int _driftMissing;
    private int _driftStale;
    private DateTimeOffset? _driftCheckedAt;

    public string DbPath => _dbPath;

    /// <summary>DI constructor: the legacy/global index, db path resolved from options.</summary>
    public RSCSqliteReportIndex(RSCReportServiceOptions options, ILogger<RSCSqliteReportIndex> logger)
        // Resolve relative DB paths under ReportsRoot so a read-only content root (Docker,
        // systemd) doesn't cause SQLITE_CANTOPEN. Absolute paths are honored verbatim.
        : this(RSCStatePaths.Resolve(options.SqliteDbPath, options.ReportsRoot),
               RSCStatePaths.Resolve(options.BackupRoot, options.ReportsRoot), options, logger)
    {
    }

    /// <summary>Explicit-path constructor used by the per-app report store factory
    /// (apps/{client}/{app}/reports.db + a sibling backups dir).</summary>
    internal RSCSqliteReportIndex(string dbPath, string backupRoot, RSCReportServiceOptions options, ILogger<RSCSqliteReportIndex> logger)
    {
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, options.SqliteCommandTimeoutSeconds);

        _dbPath = dbPath;
        var parent = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        _backupRoot = backupRoot;
        Directory.CreateDirectory(_backupRoot);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Bootstrap();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(RSCReportMetadata metadata, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO problem_reports(
    platform, file_name, submitted_at, device_model, title, email_hash,
    pharmacy_id, user_id, phone, app_version, has_attachment, size_bytes, attachment_bytes, labels_json,
    ingestion_channel, top_frame, log_summary_json, kind)
VALUES(
    @platform, @file_name, @submitted_at, @device_model, @title, @email_hash,
    @pharmacy_id, @user_id, @phone, @app_version, @has_attachment, @size_bytes, @attachment_bytes, @labels_json,
    @ingestion_channel, @top_frame, @log_summary_json, @kind)
ON CONFLICT(platform, file_name) DO UPDATE SET
    submitted_at      = excluded.submitted_at,
    device_model      = excluded.device_model,
    title             = excluded.title,
    email_hash        = excluded.email_hash,
    pharmacy_id       = excluded.pharmacy_id,
    user_id           = excluded.user_id,
    phone             = excluded.phone,
    app_version       = excluded.app_version,
    has_attachment    = excluded.has_attachment,
    size_bytes        = excluded.size_bytes,
    attachment_bytes  = excluded.attachment_bytes,
    labels_json       = excluded.labels_json,
    ingestion_channel = excluded.ingestion_channel,
    top_frame         = excluded.top_frame,
    log_summary_json  = excluded.log_summary_json,
    kind              = excluded.kind;";

        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@platform", metadata.Platform);
            cmd.Parameters.AddWithValue("@file_name", metadata.FileName);
            cmd.Parameters.AddWithValue("@submitted_at",
                metadata.SubmittedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@device_model", (object?)metadata.DeviceModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", (object?)metadata.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@email_hash", (object?)metadata.EmailHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pharmacy_id", (object?)metadata.PharmacyId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)metadata.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@phone", (object?)metadata.Phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@app_version", (object?)metadata.AppVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@has_attachment", metadata.HasAttachment ? 1 : 0);
            cmd.Parameters.AddWithValue("@size_bytes", metadata.SizeBytes);
            cmd.Parameters.AddWithValue("@attachment_bytes",
                metadata.AttachmentSizeBytes.HasValue ? (object)metadata.AttachmentSizeBytes.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@labels_json", (object?)metadata.LabelsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ingestion_channel", metadata.IngestionChannel);
            cmd.Parameters.AddWithValue("@top_frame", (object?)metadata.TopFrame ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@log_summary_json", (object?)metadata.LogSummaryJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kind", (object?)metadata.Kind ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, CancellationToken ct)
        => ListCoreAsync(platform, limit: -1, offset: 0, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, int limit, int offset, CancellationToken ct)
        => ListCoreAsync(platform, limit <= 0 ? -1 : limit, Math.Max(0, offset), ct);

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string platform, string fileName, CancellationToken ct)
    {
        const string sql = "DELETE FROM problem_reports WHERE platform = @platform AND file_name = @file_name;";

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@platform", platform);
            cmd.Parameters.AddWithValue("@file_name", fileName);

            var affected = await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            return affected > 0;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RecordLifetimeAndDeleteAsync(string platform, string fileName, CancellationToken ct)
    {
        var nowIso = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        // Read the footprint, fold it into the lifetime rollup, then delete — all in one
        // transaction so a crash can never drop a row without first banking its contribution (or
        // vice-versa). The retry wrapper is safe: an exception before COMMIT rolls the whole thing
        // back, so a replay starts from the unmodified row.
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);

            long jsonBytes = 0, attachmentBytes = 0;
            var hasAttachment = false;
            var found = false;
            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandTimeout = _commandTimeoutSeconds;
                read.CommandText = @"
SELECT size_bytes, COALESCE(attachment_bytes, 0), has_attachment
FROM problem_reports
WHERE platform = @platform AND file_name = @file_name;";
                read.Parameters.AddWithValue("@platform", platform);
                read.Parameters.AddWithValue("@file_name", fileName);
                using var reader = await read.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    found = true;
                    jsonBytes = reader.GetInt64(0);
                    attachmentBytes = reader.GetInt64(1);
                    hasAttachment = reader.GetInt64(2) != 0;
                }
            }

            if (!found)
            {
                // No row to delete (drift, or already gone). Nothing to bank, nothing to remove.
                await tx.CommitAsync(innerCt).ConfigureAwait(false);
                return false;
            }

            using (var acc = conn.CreateCommand())
            {
                acc.Transaction = tx;
                acc.CommandTimeout = _commandTimeoutSeconds;
                acc.CommandText = @"
INSERT INTO lifetime_report_stats(
    platform, deleted_reports, deleted_with_attachment, deleted_json_bytes, deleted_attachment_bytes,
    first_deleted_at, last_deleted_at)
VALUES(@platform, 1, @has_att, @json_bytes, @att_bytes, @now, @now)
ON CONFLICT(platform) DO UPDATE SET
    deleted_reports          = deleted_reports + 1,
    deleted_with_attachment  = deleted_with_attachment + @has_att,
    deleted_json_bytes       = deleted_json_bytes + @json_bytes,
    deleted_attachment_bytes = deleted_attachment_bytes + @att_bytes,
    last_deleted_at          = @now;";
                acc.Parameters.AddWithValue("@platform", platform);
                acc.Parameters.AddWithValue("@has_att", hasAttachment ? 1 : 0);
                acc.Parameters.AddWithValue("@json_bytes", jsonBytes);
                acc.Parameters.AddWithValue("@att_bytes", attachmentBytes);
                acc.Parameters.AddWithValue("@now", nowIso);
                await acc.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }

            bool deleted;
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandTimeout = _commandTimeoutSeconds;
                del.CommandText = "DELETE FROM problem_reports WHERE platform = @platform AND file_name = @file_name;";
                del.Parameters.AddWithValue("@platform", platform);
                del.Parameters.AddWithValue("@file_name", fileName);
                deleted = await del.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
            }

            await tx.CommitAsync(innerCt).ConfigureAwait(false);
            return deleted;
        }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RSCStoredReport>> ListCoreAsync(string platform, int limit, int offset, CancellationToken ct)
    {
        const string sql = @"
SELECT platform, file_name, submitted_at, size_bytes, has_attachment, attachment_bytes, ingestion_channel, top_frame, log_summary_json, kind
FROM problem_reports
WHERE platform = @platform
ORDER BY submitted_at DESC
LIMIT @limit OFFSET @offset;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCStoredReport>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@platform", platform);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            var results = new List<RSCStoredReport>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                results.Add(ReadStoredReport(reader));
            }
            return results;
        }, ct).ConfigureAwait(false);
    }

    // synchronous=NORMAL is per-connection; journal_mode=WAL persists DB-level and is set once in Bootstrap.
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The schema ladder is owned by RSCISchemaMigration classes. Bootstrap just opens the DB,
    // applies WAL once (it's a DB-level setting, not a migration), and hands off to RSCSchemaRunner.
    private static IEnumerable<RSCISchemaMigration> BuildMigrations() => new RSCISchemaMigration[]
    {
        new RSCM001_CreateProblemReports(),
        new RSCM002_AddIngestionChannel(),
        new RSCM003_AddCrashFingerprint(),
        new RSCM004_CreateForcedReports(),
        new RSCM005_DropType(),
        new RSCM006_AddLogSummary(),
        new RSCM007_AddKind(),
        new RSCM008_CreateMappings(),
        new RSCM009_MappingsAddLabel(),
        new RSCM010_MappingsDropNotes(),
        new RSCM011_DropMappings(),
        new RSCM012_CreateLifetimeStats(),
    };

    // Bootstrap is not retried: a failure here should surface during container start and fail fast.
    private void Bootstrap()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                // WAL + NORMAL sync is the recommended trade-off for a single-process server with concurrent readers.
                pragma.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }

            var runner = new RSCSchemaRunner(BuildMigrations(), _logger);
            _schemaVersion = runner.Run(conn);

            _logger.LogInformation("SQLite problem-report index ready at {Path} (schema v{Version})", _dbPath, _schemaVersion);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to bootstrap SQLite problem-report index");
            throw;
        }
    }

    // Retry with backoff for SQLITE_BUSY / SQLITE_LOCKED. Non-busy errors are logged and rethrown.
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
                _logger.LogWarning(
                    ex,
                    "SQLite busy on attempt {Attempt}/{Max}; retrying in {DelayMs}ms",
                    attempt, MaxBusyRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex, "SQLite operation failed (attempt {Attempt})", attempt);
                throw;
            }
        }
    }

    private static bool IsBusy(SqliteException ex)
        => ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    // -------- RSCIReportIndexMaintenance -------------------------------------------------------

    public async Task<RSCReportPage> SearchAsync(RSCReportFilter filter, CancellationToken ct)
    {
        var (whereSql, binder) = BuildWhereClause(filter);

        // Single snapshot: count + page both read from the same connection/transaction so totals
        // don't skew between requests.
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            int total;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandTimeout = _commandTimeoutSeconds;
                countCmd.CommandText = $"SELECT COUNT(*) FROM problem_reports {whereSql};";
                binder(countCmd);
                total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0);
            }

            var limit = filter.Limit <= 0 ? 50 : Math.Min(filter.Limit, 500);
            var offset = Math.Max(0, filter.Offset);

            using var pageCmd = conn.CreateCommand();
            pageCmd.CommandTimeout = _commandTimeoutSeconds;
            pageCmd.CommandText = $@"
SELECT platform, file_name, submitted_at, size_bytes, has_attachment, attachment_bytes, ingestion_channel, top_frame, log_summary_json, kind
FROM problem_reports
{whereSql}
ORDER BY submitted_at DESC
LIMIT @limit OFFSET @offset;";
            binder(pageCmd);
            pageCmd.Parameters.AddWithValue("@limit", limit);
            pageCmd.Parameters.AddWithValue("@offset", offset);

            var rows = new List<RSCStoredReport>(limit);
            using var reader = await pageCmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(ReadStoredReport(reader));
            }
            return new RSCReportPage(rows, total, limit, offset);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCPlatformSummary>> SummarizeAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT platform,
       COUNT(*)                        AS reports,
       COALESCE(SUM(size_bytes), 0)    AS json_bytes,
       COALESCE(SUM(attachment_bytes), 0) AS att_bytes,
       MAX(submitted_at)               AS newest
FROM problem_reports
GROUP BY platform
ORDER BY platform;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCPlatformSummary>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;

            var result = new List<RSCPlatformSummary>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                DateTimeOffset? newest = reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                result.Add(new RSCPlatformSummary(
                    Platform: reader.GetString(0),
                    ReportCount: reader.GetInt32(1),
                    TotalSizeBytes: reader.GetInt64(2),
                    TotalAttachmentBytes: reader.GetInt64(3),
                    NewestSubmittedAt: newest));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCLifetimeReportStats>> GetLifetimeStatsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT platform, deleted_reports, deleted_with_attachment, deleted_json_bytes, deleted_attachment_bytes,
       first_deleted_at, last_deleted_at
FROM lifetime_report_stats
ORDER BY platform;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCLifetimeReportStats>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;

            var result = new List<RSCLifetimeReportStats>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                result.Add(new RSCLifetimeReportStats(
                    Platform: reader.GetString(0),
                    DeletedReports: reader.GetInt64(1),
                    DeletedWithAttachment: reader.GetInt64(2),
                    DeletedJsonBytes: reader.GetInt64(3),
                    DeletedAttachmentBytes: reader.GetInt64(4),
                    FirstDeletedAt: reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5)),
                    LastDeletedAt: reader.IsDBNull(6) ? null : ParseIso(reader.GetString(6))));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCStatsReport> GetStatsAsync(DateTimeOffset from, DateTimeOffset until, int topN, CancellationToken ct)
    {
        if (until <= from) until = from.AddSeconds(1);
        topN = Math.Clamp(topN, 1, 50);
        var fromIso = from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var untilIso = until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            int total = 0, multipart = 0, json = 0;
            long totalJsonBytes = 0, totalAtt = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN ingestion_channel = 'multipart' THEN 1 ELSE 0 END), 0),
       COALESCE(SUM(CASE WHEN ingestion_channel = 'json'      THEN 1 ELSE 0 END), 0),
       COALESCE(SUM(size_bytes), 0),
       COALESCE(SUM(attachment_bytes), 0)
FROM problem_reports
WHERE submitted_at >= @from AND submitted_at < @until;";
                cmd.Parameters.AddWithValue("@from", fromIso);
                cmd.Parameters.AddWithValue("@until", untilIso);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    total = reader.GetInt32(0);
                    multipart = reader.GetInt32(1);
                    json = reader.GetInt32(2);
                    totalJsonBytes = reader.GetInt64(3);
                    totalAtt = reader.GetInt64(4);
                }
            }

            var daily = await ReadDailyAsync(conn, from, until, innerCt).ConfigureAwait(false);
            var byDevice = await ReadBucketAsync(conn, "device_model",      fromIso, untilIso, topN, innerCt).ConfigureAwait(false);
            var byPharma = await ReadBucketAsync(conn, "pharmacy_id",       fromIso, untilIso, topN, innerCt).ConfigureAwait(false);
            var byVer = await ReadBucketAsync(conn, "app_version",          fromIso, untilIso, topN, innerCt).ConfigureAwait(false);
            var byPlat = await ReadBucketAsync(conn, "platform",            fromIso, untilIso, topN, innerCt).ConfigureAwait(false);
            var byChan = await ReadBucketAsync(conn, "ingestion_channel",   fromIso, untilIso, topN, innerCt).ConfigureAwait(false);

            return new RSCStatsReport(
                From: from,
                Until: until,
                TotalReports: total,
                MultipartCount: multipart,
                JsonCount: json,
                TotalJsonBytes: totalJsonBytes,
                TotalAttachmentBytes: totalAtt,
                Daily: daily,
                ByDeviceModel: byDevice,
                ByPharmacy: byPharma,
                ByAppVersion: byVer,
                ByPlatform: byPlat,
                ByChannel: byChan);
        }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RSCDailyVolume>> ReadDailyAsync(
        SqliteConnection conn, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        // SQLite's date(...) trims any ISO-8601 timestamp to YYYY-MM-DD. Group by that, count
        // each channel, then zero-fill the buckets the operator's window expects so the chart
        // doesn't have visual gaps on quiet days.
        var rows = new Dictionary<DateOnly, (int multipart, int json)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT date(submitted_at) AS d,
       SUM(CASE WHEN ingestion_channel = 'multipart' THEN 1 ELSE 0 END),
       SUM(CASE WHEN ingestion_channel = 'json'      THEN 1 ELSE 0 END)
FROM problem_reports
WHERE submitted_at >= @from AND submitted_at < @until
GROUP BY d
ORDER BY d ASC;";
            cmd.Parameters.AddWithValue("@from", from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@until", until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (DateOnly.TryParse(reader.GetString(0), out var d))
                {
                    rows[d] = (reader.GetInt32(1), reader.GetInt32(2));
                }
            }
        }

        var startDay = DateOnly.FromDateTime(from.UtcDateTime.Date);
        var endDay = DateOnly.FromDateTime(until.UtcDateTime.Date);
        var result = new List<RSCDailyVolume>();
        for (var d = startDay; d < endDay; d = d.AddDays(1))
        {
            var (m, j) = rows.TryGetValue(d, out var v) ? v : (0, 0);
            result.Add(new RSCDailyVolume(d, m, j));
        }
        return result;
    }

    private async Task<IReadOnlyList<RSCStatsBucket>> ReadBucketAsync(
        SqliteConnection conn, string column, string fromIso, string untilIso, int topN, CancellationToken ct)
    {
        // The column name comes from a hard-coded private call site, NEVER from user input — string
        // interpolation here is safe and avoids needing dynamic SQL just to pick a GROUP BY target.
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.CommandText = $@"
SELECT COALESCE({column}, '') AS k, COUNT(*) AS n
FROM problem_reports
WHERE submitted_at >= @from AND submitted_at < @until
GROUP BY k
ORDER BY n DESC, k ASC
LIMIT @top;";
        cmd.Parameters.AddWithValue("@from", fromIso);
        cmd.Parameters.AddWithValue("@until", untilIso);
        cmd.Parameters.AddWithValue("@top", topN);

        var result = new List<RSCStatsBucket>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            if (string.IsNullOrEmpty(key)) key = "(unset)";
            result.Add(new RSCStatsBucket(key, reader.GetInt32(1)));
        }
        return result;
    }

    public async Task<RSCIndexStatusReport> GetStatusAsync(CancellationToken ct)
    {
        var fi = new FileInfo(_dbPath);
        var wal = File.Exists(_dbPath + "-wal");
        var shm = File.Exists(_dbPath + "-shm");

        // Snapshot the mutable maintenance fields under the lock so a concurrent IntegrityCheck /
        // Backup / Rebuild can't hand us a torn pair (e.g. new _lastBackupAt + old _lastBackupPath).
        string? lastIntegrityResult;
        DateTimeOffset? lastIntegrityAt, lastBackupAt, driftCheckedAt;
        string? lastBackupPath;
        int driftMissing, driftStale, schemaVersion;
        lock (_statusLock)
        {
            lastIntegrityResult = _lastIntegrityResult;
            lastIntegrityAt = _lastIntegrityAt;
            lastBackupAt = _lastBackupAt;
            lastBackupPath = _lastBackupPath;
            driftMissing = _driftMissing;
            driftStale = _driftStale;
            driftCheckedAt = _driftCheckedAt;
            schemaVersion = _schemaVersion;
        }

        // Reflect reality instead of hardcoding Healthy=true: run a cheap liveness probe and fold in
        // the last recorded integrity result. A SELECT 1 failure (DB unreadable / locked out) or a
        // non-"ok" integrity_check both surface as unhealthy with detail.
        bool healthy;
        string? healthDetail;
        try
        {
            await ExecuteWithRetryAsync(async innerCt =>
            {
                using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (lastIntegrityResult is not null && lastIntegrityResult != "ok")
            {
                healthy = false;
                healthDetail = $"last integrity_check returned: {lastIntegrityResult}";
            }
            else
            {
                healthy = true;
                healthDetail = null;
            }
        }
        catch (Exception ex)
        {
            healthy = false;
            healthDetail = $"liveness probe failed: {ex.Message}";
        }

        return new RSCIndexStatusReport(
            DbPath: _dbPath,
            Exists: fi.Exists,
            DbSizeBytes: fi.Exists ? fi.Length : 0,
            WalPresent: wal,
            ShmPresent: shm,
            SchemaVersion: schemaVersion,
            LastIntegrityCheckResult: lastIntegrityResult,
            LastIntegrityCheckAt: lastIntegrityAt,
            LastBackupAt: lastBackupAt,
            LastBackupPath: lastBackupPath,
            // Drift counters are only populated by an explicit RebuildAsync (which sets them to 0 and
            // stamps DriftCheckedAt); no standalone drift scan exists. DriftCheckedAt == null is the
            // honest "never computed" signal so a permanently-zero count can't read as "no drift".
            DriftMissingInIndex: driftMissing,
            DriftStaleIndexRows: driftStale,
            DriftCheckedAt: driftCheckedAt,
            Healthy: healthy,
            HealthDetail: healthDetail,
            HealthAt: DateTimeOffset.UtcNow);
    }

    public async Task<RSCRebuildReport> RebuildAsync(RSCIReportStore fileStore, IReadOnlyList<string> platforms, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var filesOnDisk = 0;
        var inserted = 0;
        var staleRemoved = 0;

        int indexedBefore = 0;
        foreach (var platform in platforms)
        {
            indexedBefore += (await ListAsync(platform, -1, 0, ct).ConfigureAwait(false)).Count;
        }

        foreach (var platform in platforms)
        {
            var indexRows = await ListAsync(platform, -1, 0, ct).ConfigureAwait(false);
            var indexSet = new HashSet<string>(indexRows.Select(r => r.FileName), StringComparer.Ordinal);

            var onDisk = fileStore.List(platform);
            filesOnDisk += onDisk.Count;
            var diskSet = new HashSet<string>(onDisk.Select(r => r.FileName), StringComparer.Ordinal);

            foreach (var r in onDisk)
            {
                if (indexSet.Contains(r.FileName)) continue;
                var md = new RSCReportMetadata(
                    Platform: r.Platform,
                    FileName: r.FileName,
                    SubmittedAt: r.SubmittedAt,
                    DeviceModel: null, Title: null,
                    EmailHash: null, PharmacyId: null, UserId: null, Phone: null, AppVersion: null,
                    HasAttachment: r.AttachmentFileName is not null,
                    SizeBytes: r.SizeBytes,
                    AttachmentSizeBytes: r.AttachmentSizeBytes,
                    LabelsJson: null);
                await UpsertAsync(md, ct).ConfigureAwait(false);
                inserted++;
            }

            foreach (var r in indexRows)
            {
                if (diskSet.Contains(r.FileName)) continue;
                if (await DeleteAsync(platform, r.FileName, ct).ConfigureAwait(false)) staleRemoved++;
            }
        }

        int indexedAfter = 0;
        foreach (var platform in platforms)
        {
            indexedAfter += (await ListAsync(platform, -1, 0, ct).ConfigureAwait(false)).Count;
        }

        sw.Stop();

        lock (_statusLock)
        {
            _driftMissing = 0;
            _driftStale = 0;
            _driftCheckedAt = DateTimeOffset.UtcNow;
        }

        return new RSCRebuildReport(platforms.Count, filesOnDisk, indexedBefore, indexedAfter, inserted, staleRemoved, sw.Elapsed);
    }

    public async Task<string> IntegrityCheckAsync(CancellationToken ct)
    {
        var result = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "PRAGMA integrity_check;";
            var first = (await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false))?.ToString() ?? string.Empty;
            return first;
        }, ct).ConfigureAwait(false);

        lock (_statusLock)
        {
            _lastIntegrityResult = result;
            _lastIntegrityAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    public async Task VacuumAsync(CancellationToken ct)
    {
        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = Math.Max(_commandTimeoutSeconds, 30);
            cmd.CommandText = "VACUUM; ANALYZE;";
            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task BackupAsync(string destinationPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        // Crash-safe publish: VACUUM INTO to a sibling *.partial, sanity-check the copy has the
        // expected schema + at least the current row count minus a margin, then atomically rename.
        // A crash mid-copy leaves the *.partial behind for cleanup; it never overwrites the target.
        var tempPath = destinationPath + ".partial." + Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteWithRetryAsync(async innerCt =>
            {
                using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = Math.Max(_commandTimeoutSeconds, 60);
                cmd.CommandText = "VACUUM INTO @dest;";
                cmd.Parameters.AddWithValue("@dest", tempPath);
                await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // Minimal sanity check: open the backup and run integrity_check.
            using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
            {
                verify.Open();
                using var check = verify.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                var ok = (check.ExecuteScalar() as string) == "ok";
                if (!ok) throw new InvalidOperationException("backup integrity check failed");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            lock (_statusLock)
            {
                _lastBackupAt = DateTimeOffset.UtcNow;
                _lastBackupPath = destinationPath;
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>Default backup filename used by the admin action if the operator doesn't supply one.</summary>
    public string DefaultBackupPath() => Path.Combine(_backupRoot, $"reports-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");

    private static string? HashEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(email), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (string WhereSql, Action<SqliteCommand> BindParams) BuildWhereClause(RSCReportFilter f)
    {
        var sb = new StringBuilder();
        var binders = new List<Action<SqliteCommand>>();

        void Add(string clause, string name, object? value)
        {
            if (value is null) return;
            sb.Append(sb.Length == 0 ? " WHERE " : " AND ");
            sb.Append(clause);
            binders.Add(cmd => cmd.Parameters.AddWithValue(name, value));
        }

        Add("platform = @platform", "@platform", f.Platform?.ToLowerInvariant());
        Add("pharmacy_id = @pharmacy", "@pharmacy", f.PharmacyId);
        Add("user_id = @user_id", "@user_id", f.UserId);
        Add("phone = @phone", "@phone", f.Phone);
        if (!string.IsNullOrWhiteSpace(f.Email))
        {
            // Match by hash, since we never store the raw email in the index.
            var emailHash = HashEmail(f.Email);
            Add("email_hash = @email_hash", "@email_hash", emailHash);
        }
        Add("app_version = @appv", "@appv", f.AppVersion);
        Add("ingestion_channel = @channel", "@channel", f.IngestionChannel);
        Add("top_frame = @top_frame", "@top_frame", f.TopFrame);
        if (f.HasAttachment is not null)
            Add("has_attachment = @att", "@att", f.HasAttachment.Value ? 1 : 0);
        if (!string.IsNullOrWhiteSpace(f.FileNameContains))
            Add("file_name LIKE @fn", "@fn", "%" + f.FileNameContains + "%");
        if (f.SubmittedFrom is not null)
            Add("submitted_at >= @from", "@from", f.SubmittedFrom.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        if (f.SubmittedUntil is not null)
            Add("submitted_at <= @until", "@until", f.SubmittedUntil.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        // Kind inclusion / exclusion lists. Inline parameter names to keep the placeholders
        // unique per row (binding the same name twice in a single SqliteCommand throws).
        if (f.KindIn is { Count: > 0 } kindIn)
        {
            sb.Append(sb.Length == 0 ? " WHERE " : " AND ");
            sb.Append("kind IN (");
            for (var i = 0; i < kindIn.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var pname = $"@kind_in_{i}";
                sb.Append(pname);
                var captured = kindIn[i];
                binders.Add(cmd => cmd.Parameters.AddWithValue(pname, captured));
            }
            sb.Append(')');
        }
        if (f.KindNotIn is { Count: > 0 } kindNotIn)
        {
            sb.Append(sb.Length == 0 ? " WHERE " : " AND ");
            // `kind IS NULL OR kind NOT IN (...)` — null kind (rows that predate the column)
            // is treated as "definitely not crash/analytics" and stays visible to the
            // problem-reports view.
            sb.Append("(kind IS NULL OR kind NOT IN (");
            for (var i = 0; i < kindNotIn.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var pname = $"@kind_not_in_{i}";
                sb.Append(pname);
                var captured = kindNotIn[i];
                binders.Add(cmd => cmd.Parameters.AddWithValue(pname, captured));
            }
            sb.Append("))");
        }

        return (sb.ToString(), cmd =>
        {
            foreach (var b in binders) b(cmd);
        }
        );
    }

    // Round-trip the same "O"-format UTC strings the index writes everywhere (UpsertAsync, the
    // lifetime accumulator, etc.).
    private static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.ParseExact(s, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static RSCStoredReport ReadStoredReport(SqliteDataReader reader)
    {
        var rowPlatform = reader.GetString(0);
        var fileName = reader.GetString(1);
        var submittedAt = DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var sizeBytes = reader.GetInt64(3);
        var hasAttachment = reader.GetInt64(4) != 0;
        long? attachmentBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        string? channel = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null;
        string? topFrame = reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetString(7) : null;
        string? logSummaryJson = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null;
        string? kind = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null;
        string? attachmentFileName = hasAttachment
            ? Path.GetFileNameWithoutExtension(fileName) + ".log.gz"
            : null;
        return new RSCStoredReport(rowPlatform, fileName, sizeBytes, submittedAt, attachmentFileName, attachmentBytes, channel, topFrame, logSummaryJson, kind);
    }
}
