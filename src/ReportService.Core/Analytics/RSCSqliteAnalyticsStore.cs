using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
public sealed partial class RSCSqliteAnalyticsStore : RSCIAnalyticsStore
{
    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    private const string IsoFormat = "O";

    // Tenancy defaults stamped on rows whose batch arrived without explicit attribution. MUST match
    // the literals RSCM005 backfills and RSCSqliteCatalog self-seeds, so default-attributed traffic
    // and backfilled history land in the same tenant.
    internal const string DefaultAppId = "default";
    internal const string DefaultClientId = "default";
    internal const string DefaultEnvironment = "production";

    private static string ResolveTenant(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly int _commandTimeoutSeconds;
    private readonly int _hashVersion;
    private readonly ILogger<RSCSqliteAnalyticsStore> _logger;
    // Lowercased copy of the validator's forbidden-property deny-list. The store redacts any value
    // under one of these keys out of every dead-letter row before it lands at rest — defence in
    // depth for batch-level rejects (e.g. platform_unknown), where the whole event JSON is captured
    // without the per-event PII guard having run. See RedactRawForDeadLetter.
    private readonly HashSet<string> _forbiddenKeys;
    private int _schemaVersion;

    public string DbPath => _dbPath;
    public int SchemaVersion => _schemaVersion;

    /// <summary>DI constructor: resolves the single shared analytics DB path from options. In the
    /// database-per-app model this is the legacy/default store; per-app stores are built through the
    /// explicit-path constructor by <see cref="RSCSqliteAnalyticsStoreFactory"/>.</summary>
    public RSCSqliteAnalyticsStore(
        RSCReportServiceOptions reportOptions,
        RSCAnalyticsOptions analyticsOptions,
        ILogger<RSCSqliteAnalyticsStore> logger)
        : this(RSCStatePaths.Resolve(analyticsOptions.SqliteDbPath, reportOptions.ReportsRoot), analyticsOptions, logger)
    {
    }

    /// <summary>Explicit-path constructor used by the per-app factory: the caller supplies the exact
    /// (already-resolved) DB path — e.g. <c>{ReportsRoot}/apps/{client}/{app}/analytics.db</c> — and
    /// the same migration ladder runs against it.</summary>
    internal RSCSqliteAnalyticsStore(
        string dbPath,
        RSCAnalyticsOptions analyticsOptions,
        ILogger<RSCSqliteAnalyticsStore> logger)
    {
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, analyticsOptions.SqliteCommandTimeoutSeconds);
        _hashVersion = analyticsOptions.IdentifierHashVersion;
        _forbiddenKeys = new HashSet<string>(
            (analyticsOptions.ForbiddenPropertyKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        _dbPath = dbPath;
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

    /// <summary>Close the pooled connections held for this store's DB so the underlying file can be
    /// deleted (used when the owning app/client is purged). Microsoft.Data.Sqlite pools by connection
    /// string, so this clears exactly this DB's pool and nothing else.</summary>
    internal void EvictPooledConnections()
    {
        using var conn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(conn);
    }

    private static IEnumerable<RSCISchemaMigration> BuildMigrations() => new RSCISchemaMigration[]
    {
        new RSCM001_CreateAnalyticsTables(),
        new RSCM002_CreateRetentionAndFunnelTables(),
        new RSCM003_AddEventIdIndex(),
        new RSCM004_RekeyEventIdIndexAndFunnelSteps(),
        new RSCM005_AddTenancyColumns(),
        new RSCM006_ScopeBatchesIdempotency(),
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

    private static DateTimeOffset? ParseTolerant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
    }

    /// <summary>
    /// Builds the tenancy + platform filter shared by every read query: one leading-AND clause per
    /// non-null scope component (so an all-null scope yields an empty clause — "return everything",
    /// the backward-compat path). Column names are bare, which is safe because every analytics read
    /// targets a single table (no joins / aliases). The binder must be applied once per command.
    /// </summary>
    private static (string Clause, Action<SqliteCommand> Binder) BuildScopeClause(RSCAnalyticsScope scope)
    {
        var clauses = new List<string>(4);
        var binders = new List<Action<SqliteCommand>>(4);

        void Add(string column, string param, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var lower = value.ToLowerInvariant();
            clauses.Add($" AND {column} = {param}");
            binders.Add(cmd => cmd.Parameters.AddWithValue(param, lower));
        }

        Add("app_id", "@scope_app", scope.AppId);
        Add("environment", "@scope_env", scope.Environment);
        Add("client_id", "@scope_client", scope.ClientId);
        Add("platform", "@scope_platform", scope.Platform);

        return (string.Concat(clauses), cmd => { foreach (var b in binders) b(cmd); });
    }
}
