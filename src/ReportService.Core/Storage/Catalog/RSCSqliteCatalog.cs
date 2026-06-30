using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Migrations;
using ReportService.Storage.Migrations.Catalog;

namespace ReportService.Storage.Catalog;

/// <summary>
/// SQLite-backed <see cref="RSCICatalog"/>. Mirrors the resilience posture of
/// <see cref="ReportService.Storage.ApiKeys.RSCSqliteApiKeyStore"/>: a graceful <c>_ready</c>
/// bootstrap, WAL + busy-timeout, SQLITE_BUSY retry on mutations, and a lock-free in-memory
/// validation snapshot rebuilt at construction and after every mutation.
///
/// The hot path (<see cref="IsValidApp"/> / <see cref="IsValidEnvironment"/> / <see cref="IsValidClient"/>)
/// never touches the DB. If the DB is unavailable, validation fails closed (every batch is rejected
/// while <c>Catalog:Enabled</c> is true — flip it off to fall back to default attribution) and
/// <see cref="RSCComponentHealth"/> is marked degraded so the Status page surfaces it.
/// </summary>
public sealed class RSCSqliteCatalog : RSCICatalog
{
    public const string Component = "CatalogDb";

    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    private const int BusyTimeoutMs = 5_000;

    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;
    private readonly RSCComponentHealth _health;
    private readonly ILogger<RSCSqliteCatalog> _logger;
    private readonly bool _ready;

    // Lock-free validation snapshots, replaced wholesale on mutation. Active rows only.
    // Apps are keyed by (client slug, app slug) since app slugs are unique only within a client.
    private volatile IReadOnlyDictionary<(string Client, string App), IReadOnlySet<string>> _appEnvs =
        new Dictionary<(string, string), IReadOnlySet<string>>();
    private volatile IReadOnlySet<string> _clientSlugs = new HashSet<string>(StringComparer.Ordinal);

    public RSCSqliteCatalog(
        RSCReportServiceOptions reportOptions,
        RSCCatalogOptions catalogOptions,
        RSCComponentHealth health,
        ILogger<RSCSqliteCatalog> logger)
    {
        _health = health;
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, catalogOptions.SqliteCommandTimeoutSeconds);

        var dbPath = RSCStatePaths.Resolve(catalogOptions.SqliteDbPath, reportOptions.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(parent))
        {
            try { Directory.CreateDirectory(parent); }
            catch (Exception ex)
            {
                _connectionString = string.Empty;
                _logger.LogWarning(ex, "Catalog DB directory unavailable; tenancy validation will fail closed");
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
                new RSCISchemaMigration[] { new RSCMA001_CreateCatalog(), new RSCMA002_NestAppsUnderClients() }, _logger);
            var version = runner.Run(conn);

            SeedDefaultsAndOptions(conn, catalogOptions);

            _ready = true;
            RebuildCache(conn);
            _health.MarkHealthy(Component, $"schema v{version}, {_appEnvs.Count} apps, {_clientSlugs.Count} clients");
            _logger.LogInformation("Catalog ready at {Path} (schema v{Version}, {Apps} apps, {Clients} clients)",
                dbPath, version, _appEnvs.Count, _clientSlugs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog bootstrap failed at {Path}; tenancy validation will fail closed", dbPath);
            _health.MarkDegraded(Component, $"bootstrap failed: {ex.Message}", ex);
        }
    }

    // -------- Hot-path validation --------

    public bool IsValidClient(string clientSlug)
        => _ready && _clientSlugs.Contains(RSCCatalogSlug.Normalize(clientSlug));

    public bool IsValidApp(string clientSlug, string appSlug)
        => _ready && _appEnvs.ContainsKey((RSCCatalogSlug.Normalize(clientSlug), RSCCatalogSlug.Normalize(appSlug)));

    public bool IsValidEnvironment(string clientSlug, string appSlug, string environment)
        => _ready
           && _appEnvs.TryGetValue((RSCCatalogSlug.Normalize(clientSlug), RSCCatalogSlug.Normalize(appSlug)), out var envs)
           && envs.Contains(RSCCatalogSlug.Normalize(environment));

    // -------- Apps (client-scoped) --------

    public Task<IReadOnlyList<RSCAppRecord>> ListAppsAsync(string clientSlug, bool includeArchived, CancellationToken ct)
        => QueryAppsAsync(RSCCatalogSlug.Normalize(clientSlug), includeArchived, ct);

    public Task<IReadOnlyList<RSCAppRecord>> ListAllAppsAsync(bool includeArchived, CancellationToken ct)
        => QueryAppsAsync(clientFilter: null, includeArchived, ct);

    private async Task<IReadOnlyList<RSCAppRecord>> QueryAppsAsync(string? clientFilter, bool includeArchived, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<RSCAppRecord>();
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAppRecord>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            var conditions = new List<string>();
            if (!includeArchived) conditions.Add("a.archived_at IS NULL");
            if (clientFilter is not null) conditions.Add("a.client_id = @client");
            var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $@"
SELECT a.id, a.client_id, a.slug, a.display_name, a.created_at, a.archived_at, e.environment
FROM apps a LEFT JOIN app_environments e ON e.app_id = a.id{where}
ORDER BY a.client_id ASC, a.created_at DESC, e.environment ASC;";
            if (clientFilter is not null) cmd.Parameters.AddWithValue("@client", clientFilter);

            var byId = new Dictionary<string, (RSCAppRecord Rec, List<string> Envs)>(StringComparer.Ordinal);
            var order = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!byId.TryGetValue(id, out var entry))
                {
                    var rec = new RSCAppRecord(
                        Id: id,
                        ClientSlug: reader.GetString(1),
                        Slug: reader.GetString(2),
                        DisplayName: reader.GetString(3),
                        CreatedAt: ParseTs(reader.GetString(4)) ?? DateTimeOffset.UtcNow,
                        ArchivedAt: reader.IsDBNull(5) ? null : ParseTs(reader.GetString(5)),
                        Environments: Array.Empty<string>());
                    entry = (rec, new List<string>());
                    byId[id] = entry;
                    order.Add(id);
                }
                if (!reader.IsDBNull(6)) entry.Envs.Add(reader.GetString(6));
            }
            return order.Select(id =>
            {
                var (rec, envs) = byId[id];
                return rec with { Environments = envs };
            }).ToList();
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCAppRecord?> GetAppAsync(string clientSlug, string appSlug, CancellationToken ct)
    {
        var normalized = RSCCatalogSlug.Normalize(appSlug);
        var apps = await ListAppsAsync(clientSlug, includeArchived: true, ct).ConfigureAwait(false);
        return apps.FirstOrDefault(a => string.Equals(a.Slug, normalized, StringComparison.Ordinal));
    }

    public async Task<RSCAppRecord> CreateAppAsync(
        string clientSlug, string appSlug, string displayName, IReadOnlyList<string> environments, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Require(clientSlug, "Client slug");
        if (!IsValidClient(normClient))
            throw new RSCCatalogException($"Unknown client '{normClient}'. Register the client before adding apps to it.");
        var normSlug = RSCCatalogSlug.Require(appSlug, "App slug");
        var name = string.IsNullOrWhiteSpace(displayName) ? normSlug : displayName.Trim();
        var envs = NormalizeEnvironments(environments);
        if (envs.Count == 0)
            throw new RSCCatalogException("An app needs at least one environment (e.g. production).");

        var id = NewId("app");
        var createdAt = DateTimeOffset.UtcNow;

        try
        {
            await ExecuteWithRetryAsync(async innerCt =>
            {
                using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
                using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = _commandTimeoutSeconds;
                    cmd.CommandText = @"
INSERT INTO apps(id, client_id, slug, display_name, created_at, archived_at)
VALUES(@id, @client, @slug, @name, @created_at, NULL);";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@client", normClient);
                    cmd.Parameters.AddWithValue("@slug", normSlug);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@created_at", Format(createdAt));
                    await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                }
                foreach (var env in envs)
                    await InsertEnvironmentAsync(conn, tx, id, env, createdAt, innerCt).ConfigureAwait(false);
                await tx.CommitAsync(innerCt).ConfigureAwait(false);
                return 0;
            }, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsConstraint(ex))
        {
            throw new RSCCatalogException($"Client '{normClient}' already has an app with slug '{normSlug}'.");
        }

        RebuildCacheSafe();
        return new RSCAppRecord(id, normClient, normSlug, name, createdAt, null, envs);
    }

    public Task<bool> RenameAppAsync(string clientSlug, string appSlug, string displayName, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? throw new RSCCatalogException("Display name is required.")
            : displayName.Trim();
        return ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "UPDATE apps SET display_name = @name WHERE client_id = @client AND slug = @slug;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@client", normClient);
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct);
        // No cache rebuild: display name isn't part of the validation snapshot.
    }

    public async Task<bool> AddEnvironmentAsync(string clientSlug, string appSlug, string environment, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        var env = RSCCatalogSlug.Require(environment, "Environment");
        var added = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            var appId = await GetActiveAppIdAsync(conn, null, normClient, normSlug, innerCt).ConfigureAwait(false);
            if (appId is null) return false;
            await InsertEnvironmentAsync(conn, null, appId, env, DateTimeOffset.UtcNow, innerCt).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        if (added) RebuildCacheSafe();
        return added;
    }

    public async Task<bool> RemoveEnvironmentAsync(string clientSlug, string appSlug, string environment, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        var env = RSCCatalogSlug.Normalize(environment);
        var removed = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            var appId = await GetActiveAppIdAsync(conn, null, normClient, normSlug, innerCt).ConfigureAwait(false);
            if (appId is null) return false;

            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM app_environments WHERE app_id = @id;";
                count.Parameters.AddWithValue("@id", appId);
                if (Convert.ToInt32(await count.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0) <= 1)
                    return false; // refuse to strip the last environment
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM app_environments WHERE app_id = @id AND environment = @env;";
            cmd.Parameters.AddWithValue("@id", appId);
            cmd.Parameters.AddWithValue("@env", env);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
        if (removed) RebuildCacheSafe();
        return removed;
    }

    public async Task<bool> ArchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        var archived = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE apps SET archived_at = @now WHERE client_id = @client AND slug = @slug AND archived_at IS NULL;";
            cmd.Parameters.AddWithValue("@now", Format(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("@client", normClient);
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
        if (archived) RebuildCacheSafe();
        return archived;
    }

    // -------- Clients --------

    public async Task<IReadOnlyList<RSCClientRecord>> ListClientsAsync(bool includeArchived, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<RSCClientRecord>();
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCClientRecord>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            var where = includeArchived ? string.Empty : " WHERE archived_at IS NULL";
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $"SELECT id, slug, display_name, created_at, archived_at FROM clients{where} ORDER BY created_at DESC;";
            var rows = new List<RSCClientRecord>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCClientRecord(
                    Id: reader.GetString(0),
                    Slug: reader.GetString(1),
                    DisplayName: reader.GetString(2),
                    CreatedAt: ParseTs(reader.GetString(3)) ?? DateTimeOffset.UtcNow,
                    ArchivedAt: reader.IsDBNull(4) ? null : ParseTs(reader.GetString(4))));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCClientRecord?> GetClientAsync(string slug, CancellationToken ct)
    {
        var normalized = RSCCatalogSlug.Normalize(slug);
        var clients = await ListClientsAsync(includeArchived: true, ct).ConfigureAwait(false);
        return clients.FirstOrDefault(c => string.Equals(c.Slug, normalized, StringComparison.Ordinal));
    }

    public async Task<RSCClientRecord> CreateClientAsync(string slug, string displayName, CancellationToken ct)
    {
        EnsureReady();
        var normSlug = RSCCatalogSlug.Require(slug, "Client slug");
        var name = string.IsNullOrWhiteSpace(displayName) ? normSlug : displayName.Trim();
        var id = NewId("cli");
        var createdAt = DateTimeOffset.UtcNow;

        try
        {
            await ExecuteWithRetryAsync(async innerCt =>
            {
                using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
INSERT INTO clients(id, slug, display_name, created_at, archived_at)
VALUES(@id, @slug, @name, @created_at, NULL);";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@slug", normSlug);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@created_at", Format(createdAt));
                await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
                return 0;
            }, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsConstraint(ex))
        {
            throw new RSCCatalogException($"A client with slug '{normSlug}' already exists.");
        }

        RebuildCacheSafe();
        return new RSCClientRecord(id, normSlug, name, createdAt, null);
    }

    public Task<bool> RenameClientAsync(string slug, string displayName, CancellationToken ct)
    {
        EnsureReady();
        var normSlug = RSCCatalogSlug.Normalize(slug);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? throw new RSCCatalogException("Display name is required.")
            : displayName.Trim();
        return ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clients SET display_name = @name WHERE slug = @slug;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct);
    }

    public async Task<bool> ArchiveClientAsync(string slug, CancellationToken ct)
    {
        EnsureReady();
        var normSlug = RSCCatalogSlug.Normalize(slug);
        var archived = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clients SET archived_at = @now WHERE slug = @slug AND archived_at IS NULL;";
            cmd.Parameters.AddWithValue("@now", Format(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
        if (archived) RebuildCacheSafe();
        return archived;
    }

    public Task<int> CountActiveAppsAsync(CancellationToken ct) => CountAsync("apps", ct);
    public Task<int> CountActiveClientsAsync(CancellationToken ct) => CountAsync("clients", ct);

    private async Task<int> CountAsync(string table, CancellationToken ct)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = await OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE archived_at IS NULL;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }
        catch { return 0; }
    }

    // -------- Bootstrap seeding --------

    private void SeedDefaultsAndOptions(SqliteConnection conn, RSCCatalogOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        var defaultClient = RSCCatalogSlug.Normalize(options.DefaultClientSlug);

        // Always-present default client + its default app/env so attribution-omitting (older) SDK
        // builds, and any key with no client binding, still resolve.
        SeedClientIfMissing(conn, defaultClient, "Default client", now);
        SeedAppIfMissing(conn, defaultClient, RSCCatalogSlug.Normalize(options.DefaultAppSlug), "Default app",
            new[] { RSCCatalogSlug.Normalize(options.DefaultEnvironment) }, now);

        // Config-declared client seeds first (dev appsettings supplies demo clients).
        foreach (var client in options.SeedClients)
        {
            var slug = RSCCatalogSlug.Normalize(client.Slug);
            if (!RSCCatalogSlug.IsValid(slug)) continue;
            SeedClientIfMissing(conn, slug, string.IsNullOrWhiteSpace(client.DisplayName) ? slug : client.DisplayName.Trim(), now);
        }

        // Config-declared app seeds, each owned by a client (App A / App B in dev). Apps whose
        // ClientSlug is blank or unregistered fall back to the default client.
        foreach (var app in options.SeedApps)
        {
            var slug = RSCCatalogSlug.Normalize(app.Slug);
            if (!RSCCatalogSlug.IsValid(slug)) continue;
            var owner = RSCCatalogSlug.Normalize(app.ClientSlug);
            if (!RSCCatalogSlug.IsValid(owner) || !ClientExists(conn, owner)) owner = defaultClient;
            var envs = NormalizeEnvironments(app.Environments);
            if (envs.Count == 0) envs = new List<string> { RSCCatalogSlug.Normalize(options.DefaultEnvironment) };
            SeedAppIfMissing(conn, owner, slug, string.IsNullOrWhiteSpace(app.DisplayName) ? slug : app.DisplayName.Trim(), envs, now);
        }
    }

    private static bool ClientExists(SqliteConnection conn, string slug)
    {
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT 1 FROM clients WHERE slug = @slug;";
        find.Parameters.AddWithValue("@slug", slug);
        return find.ExecuteScalar() is not null;
    }

    private void SeedAppIfMissing(SqliteConnection conn, string clientSlug, string slug, string displayName, IReadOnlyList<string> envs, DateTimeOffset now)
    {
        string? appId;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT id FROM apps WHERE client_id = @client AND slug = @slug;";
            find.Parameters.AddWithValue("@client", clientSlug);
            find.Parameters.AddWithValue("@slug", slug);
            appId = (string?)find.ExecuteScalar();
        }
        if (appId is null)
        {
            appId = NewId("app");
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO apps(id, client_id, slug, display_name, created_at, archived_at) VALUES(@id,@client,@slug,@name,@at,NULL);";
            ins.Parameters.AddWithValue("@id", appId);
            ins.Parameters.AddWithValue("@client", clientSlug);
            ins.Parameters.AddWithValue("@slug", slug);
            ins.Parameters.AddWithValue("@name", displayName);
            ins.Parameters.AddWithValue("@at", Format(now));
            ins.ExecuteNonQuery();
        }
        foreach (var env in envs)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR IGNORE INTO app_environments(app_id, environment, created_at) VALUES(@id,@env,@at);";
            ins.Parameters.AddWithValue("@id", appId);
            ins.Parameters.AddWithValue("@env", env);
            ins.Parameters.AddWithValue("@at", Format(now));
            ins.ExecuteNonQuery();
        }
    }

    private void SeedClientIfMissing(SqliteConnection conn, string slug, string displayName, DateTimeOffset now)
    {
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT 1 FROM clients WHERE slug = @slug;";
        find.Parameters.AddWithValue("@slug", slug);
        if (find.ExecuteScalar() is not null) return;
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO clients(id, slug, display_name, created_at, archived_at) VALUES(@id,@slug,@name,@at,NULL);";
        ins.Parameters.AddWithValue("@id", NewId("cli"));
        ins.Parameters.AddWithValue("@slug", slug);
        ins.Parameters.AddWithValue("@name", displayName);
        ins.Parameters.AddWithValue("@at", Format(now));
        ins.ExecuteNonQuery();
    }

    // -------- Internals --------

    private static async Task<string?> GetActiveAppIdAsync(
        SqliteConnection conn, SqliteTransaction? tx, string clientSlug, string appSlug, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM apps WHERE client_id = @client AND slug = @slug AND archived_at IS NULL;";
        cmd.Parameters.AddWithValue("@client", clientSlug);
        cmd.Parameters.AddWithValue("@slug", appSlug);
        return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertEnvironmentAsync(
        SqliteConnection conn, SqliteTransaction? tx, string appId, string env, DateTimeOffset at, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO app_environments(app_id, environment, created_at) VALUES(@id,@env,@at);";
        cmd.Parameters.AddWithValue("@id", appId);
        cmd.Parameters.AddWithValue("@env", env);
        cmd.Parameters.AddWithValue("@at", Format(at));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static List<string> NormalizeEnvironments(IEnumerable<string>? environments) =>
        (environments ?? Array.Empty<string>())
            .Select(RSCCatalogSlug.Normalize)
            .Where(RSCCatalogSlug.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 1 + 12)];

    private void EnsureReady()
    {
        if (!_ready)
            throw new RSCCatalogException("The catalog store is unavailable (DB bootstrap failed); cannot register apps or clients.");
    }

    private void RebuildCacheSafe()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            RebuildCache(conn);
            _health.MarkHealthy(Component, $"{_appEnvs.Count} apps, {_clientSlugs.Count} clients cached");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog cache reload failed; serving previous snapshot");
            _health.MarkDegraded(Component, $"cache reload failed: {ex.Message}", ex);
        }
    }

    private void RebuildCache(SqliteConnection conn)
    {
        var apps = new Dictionary<(string, string), IReadOnlySet<string>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT a.client_id, a.slug, e.environment FROM apps a
LEFT JOIN app_environments e ON e.app_id = a.id
WHERE a.archived_at IS NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!apps.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    apps[key] = set;
                }
                if (!reader.IsDBNull(2)) ((HashSet<string>)set).Add(reader.GetString(2));
            }
        }

        var clients = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM clients WHERE archived_at IS NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) clients.Add(reader.GetString(0));
        }

        _appEnvs = apps;
        _clientSlugs = clients;
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
                _logger.LogWarning(ex, "Catalog SQLite busy on attempt {Attempt}/{Max}; retrying in {DelayMs}ms", attempt, MaxBusyRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
        }
    }

    private static bool IsBusy(SqliteException ex)
        => ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static bool IsConstraint(SqliteException ex)
        => ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */;

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTs(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
