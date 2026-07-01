using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage.Migrations;
using ReportService.Storage.Migrations.ApiKeys;

namespace ReportService.Storage.ApiKeys;

/// <summary>
/// SQLite-backed <see cref="RSCIApiKeyStore"/>. Mirrors the resilience posture of
/// <see cref="RSCSqliteForcedReportStore"/> / <see cref="ReportService.Audit.RSCSqliteAuditLog"/>:
/// a graceful <c>_ready</c> bootstrap, WAL + busy-timeout, and SQLITE_BUSY retry on mutations.
///
/// The hot path (<see cref="Resolve"/>) never touches the DB — all keys are held in a lock-free
/// in-memory snapshot keyed by hash, rebuilt at construction and after every mutation. If the DB is
/// unavailable, DB-key auth fails closed (the static root key still works) and
/// <see cref="RSCComponentHealth"/> is marked degraded so the Status page surfaces it.
/// </summary>
public sealed class RSCSqliteApiKeyStore : RSCIApiKeyStore
{
    public const string Component = "ApiKeysDb";

    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    private const int BusyTimeoutMs = 5_000;

    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;
    private readonly RSCComponentHealth _health;
    private readonly ILogger<RSCSqliteApiKeyStore> _logger;
    private readonly bool _ready;

    // Lock-free read snapshot: presented-key-hash -> record. Replaced wholesale on mutation.
    private volatile IReadOnlyDictionary<string, RSCApiKeyRecord> _cache =
        new Dictionary<string, RSCApiKeyRecord>(StringComparer.Ordinal);

    public RSCSqliteApiKeyStore(
        RSCReportServiceOptions options,
        RSCComponentHealth health,
        ILogger<RSCSqliteApiKeyStore> logger)
    {
        _health = health;
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, options.SqliteCommandTimeoutSeconds);

        var dbPath = RSCStatePaths.Resolve(options.ApiKeysDbPath, options.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(parent))
        {
            try { Directory.CreateDirectory(parent); }
            catch (Exception ex)
            {
                _connectionString = string.Empty;
                _logger.LogWarning(ex, "API-key DB directory unavailable; DB-backed keys disabled (static root key still works)");
                _health.MarkDegraded(Component, $"directory unavailable: {ex.Message}", ex);
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

            var runner = new RSCSchemaRunner(
                new RSCISchemaMigration[] { new RSCMK001_CreateApiKeys(), new RSCMK002_AddClientBinding(), new RSCMK003_RenameUserRoleToClient() }, _logger);
            var version = runner.Run(conn);

            _ready = true;
            RebuildCache(conn);
            _health.MarkHealthy(Component, $"schema v{version}, {_cache.Count} keys cached");
            _logger.LogInformation("API-key store ready at {Path} (schema v{Version}, {Count} keys)", dbPath, version, _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API-key store bootstrap failed at {Path}; DB-backed keys disabled (static root key still works)", dbPath);
            _health.MarkDegraded(Component, $"bootstrap failed: {ex.Message}", ex);
        }
    }

    public RSCApiKeyRecord? Resolve(string presentedKey)
    {
        if (!_ready || string.IsNullOrEmpty(presentedKey)) return null;

        var hash = RSCApiKeyGenerator.Hash(presentedKey);
        if (!_cache.TryGetValue(hash, out var rec)) return null;

        var now = DateTimeOffset.UtcNow;
        if (rec.RevokedAt is not null) return null;
        if (rec.ExpiresAt is { } exp && exp <= now) return null;
        return rec;
    }

    public async Task<RSCApiKeyCreated> CreateAsync(
        string role,
        string? label,
        DateTimeOffset? expiresAt,
        int? rateLimitPerMinute,
        string createdBy,
        CancellationToken ct,
        string? clientId = null)
    {
        if (!RSCApiKeyRoles.IsValid(role))
            throw new ArgumentException($"role must be '{RSCApiKeyRoles.Admin}' or '{RSCApiKeyRoles.Client}'", nameof(role));
        if (rateLimitPerMinute is < 1)
            throw new ArgumentException("rateLimitPerMinute must be >= 1 when set", nameof(rateLimitPerMinute));
        if (expiresAt is { } e && e <= DateTimeOffset.UtcNow)
            throw new ArgumentException("expiresAt must be in the future", nameof(expiresAt));
        EnsureReady();

        var normalizedClient = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim().ToLowerInvariant();

        // Role ⇔ binding invariant — the two-role model has no unbound non-admin ("legacy") key:
        // a client key is scoped to exactly one client and MUST be bound; an admin key spans all
        // clients and MUST be unbound.
        if (role == RSCApiKeyRoles.Client && normalizedClient is null)
            throw new ArgumentException("a client key must be bound to a client (clientId is required)", nameof(clientId));
        if (role == RSCApiKeyRoles.Admin && normalizedClient is not null)
            throw new ArgumentException("an admin key spans all clients and must not be bound to one", nameof(clientId));
        var gen = RSCApiKeyGenerator.Create();
        var createdAt = DateTimeOffset.UtcNow;

        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
INSERT INTO api_keys(id, key_hash, role, label, created_at, created_by, expires_at, revoked_at, rate_limit_per_minute, last_used_at, client_id)
VALUES(@id, @hash, @role, @label, @created_at, @created_by, @expires_at, NULL, @rate, NULL, @client_id);";
            cmd.Parameters.AddWithValue("@id", gen.Id);
            cmd.Parameters.AddWithValue("@hash", gen.Hash);
            cmd.Parameters.AddWithValue("@role", role);
            cmd.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", Format(createdAt));
            cmd.Parameters.AddWithValue("@created_by", (object?)createdBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@expires_at", expiresAt is { } exp ? Format(exp) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rate", (object?)rateLimitPerMinute ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@client_id", (object?)normalizedClient ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);

        RebuildCacheSafe();

        var meta = new RSCApiKeyMetadata(
            Id: gen.Id, Role: role, Label: label, CreatedAt: createdAt, CreatedBy: createdBy,
            ExpiresAt: expiresAt, RevokedAt: null, RateLimitPerMinute: rateLimitPerMinute, LastUsedAt: null,
            ClientId: normalizedClient);
        return new RSCApiKeyCreated(meta, gen.PlaintextKey);
    }

    public async Task<IReadOnlyList<RSCApiKeyMetadata>> ListAsync(CancellationToken ct)
    {
        if (!_ready) return Array.Empty<RSCApiKeyMetadata>();

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCApiKeyMetadata>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT id, role, label, created_at, created_by, expires_at, revoked_at, rate_limit_per_minute, last_used_at, client_id
FROM api_keys ORDER BY created_at DESC;";

            var rows = new List<RSCApiKeyMetadata>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCApiKeyMetadata(
                    Id: reader.GetString(0),
                    Role: reader.GetString(1),
                    Label: reader.IsDBNull(2) ? null : reader.GetString(2),
                    CreatedAt: ParseTs(reader.GetString(3)) ?? DateTimeOffset.UtcNow,
                    CreatedBy: reader.IsDBNull(4) ? null : reader.GetString(4),
                    ExpiresAt: reader.IsDBNull(5) ? null : ParseTs(reader.GetString(5)),
                    RevokedAt: reader.IsDBNull(6) ? null : ParseTs(reader.GetString(6)),
                    RateLimitPerMinute: reader.IsDBNull(7) ? null : (int)reader.GetInt64(7),
                    LastUsedAt: reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
                    ClientId: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(string id, string revokedBy, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return false;
        EnsureReady();

        var revoked = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            // Only revoke an active row so a double-revoke (or unknown id) reports false.
            cmd.CommandText = "UPDATE api_keys SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL;";
            cmd.Parameters.AddWithValue("@now", Format(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("@id", id);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);

        if (revoked) RebuildCacheSafe();
        return revoked;
    }

    public async Task<int> CountActiveAsync(CancellationToken ct)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = await OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM api_keys WHERE revoked_at IS NULL AND (expires_at IS NULL OR expires_at > @now);";
            cmd.Parameters.AddWithValue("@now", Format(DateTimeOffset.UtcNow));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private void EnsureReady()
    {
        if (!_ready)
            throw new InvalidOperationException("API-key store is unavailable (DB bootstrap failed); cannot create or revoke keys.");
    }

    private void RebuildCacheSafe()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            RebuildCache(conn);
            _health.MarkHealthy(Component, $"{_cache.Count} keys cached");
        }
        catch (Exception ex)
        {
            // A failed reload leaves the previous snapshot in place — stale but safe (revoked keys are
            // still rejected by the DB on the next mutation, and Resolve re-checks expiry/revoke).
            _logger.LogWarning(ex, "API-key cache reload failed; serving previous snapshot");
            _health.MarkDegraded(Component, $"cache reload failed: {ex.Message}", ex);
        }
    }

    private void RebuildCache(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key_hash, id, role, rate_limit_per_minute, expires_at, revoked_at, client_id FROM api_keys WHERE revoked_at IS NULL;";

        var next = new Dictionary<string, RSCApiKeyRecord>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var hash = reader.GetString(0);
            next[hash] = new RSCApiKeyRecord(
                Id: reader.GetString(1),
                Role: reader.GetString(2),
                RateLimitPerMinute: reader.IsDBNull(3) ? null : (int)reader.GetInt64(3),
                ExpiresAt: reader.IsDBNull(4) ? null : ParseTs(reader.GetString(4)),
                RevokedAt: null,
                ClientId: reader.IsDBNull(6) ? null : reader.GetString(6));
        }
        _cache = next;
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
                _logger.LogWarning(ex, "API-key store SQLite busy on attempt {Attempt}/{Max}; retrying in {DelayMs}ms", attempt, MaxBusyRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex, "API-key store SQLite operation failed (attempt {Attempt})", attempt);
                throw;
            }
        }
    }

    private static bool IsBusy(SqliteException ex)
        => ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // Tolerant parser: rows are written with round-trip "O", but a hand-edited row may omit fractional
    // seconds. Returns null on garbage so a single bad row degrades rather than throwing.
    private static DateTimeOffset? ParseTs(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
