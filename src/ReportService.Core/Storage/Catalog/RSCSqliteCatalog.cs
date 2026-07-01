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
/// The hot path (<see cref="IsValidApp"/> / <see cref="IsValidClient"/>)
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

    // The seeded fallback tenant — protected from archive/delete so attribution-omitting traffic and
    // unbound/root keys always have a client/app to land on.
    private readonly string _defaultClient;
    private readonly string _defaultApp;

    // Lock-free validation snapshots, replaced wholesale on mutation. Active rows only.
    // Apps are keyed by (client slug, app slug); the slug carries any environment distinction
    // (env is folded into the slug — no separate environment axis).
    private volatile IReadOnlySet<(string Client, string App)> _apps =
        new HashSet<(string, string)>();
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
        _defaultClient = DefaultOrFallback(catalogOptions.DefaultClientSlug);
        _defaultApp = DefaultOrFallback(catalogOptions.DefaultAppSlug);

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
                new RSCISchemaMigration[]
                {
                    new RSCMA001_CreateCatalog(),
                    new RSCMA002_NestAppsUnderClients(),
                    new RSCMA003_DropAppEnvironments(),
                }, _logger);
            var version = runner.Run(conn);

            SeedDefaultsAndOptions(conn, catalogOptions);

            _ready = true;
            RebuildCache(conn);
            _health.MarkHealthy(Component, $"schema v{version}, {_apps.Count} apps, {_clientSlugs.Count} clients");
            _logger.LogInformation("Catalog ready at {Path} (schema v{Version}, {Apps} apps, {Clients} clients)",
                dbPath, version, _apps.Count, _clientSlugs.Count);
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
        => _ready && _apps.Contains((RSCCatalogSlug.Normalize(clientSlug), RSCCatalogSlug.Normalize(appSlug)));

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
            // Active listing (fan-out dashboards): an app is hidden if it OR its owning client is
            // archived — so an archived client's data disappears from every read. The admin console
            // passes includeArchived:true to still manage them.
            if (!includeArchived) conditions.Add("a.archived_at IS NULL AND c.archived_at IS NULL");
            if (clientFilter is not null) conditions.Add("a.client_id = @client");
            var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $@"
SELECT a.id, a.client_id, a.slug, a.display_name, a.created_at, a.archived_at
FROM apps a JOIN clients c ON c.slug = a.client_id{where}
ORDER BY a.client_id ASC, a.created_at DESC, a.slug ASC;";
            if (clientFilter is not null) cmd.Parameters.AddWithValue("@client", clientFilter);

            var rows = new List<RSCAppRecord>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCAppRecord(
                    Id: reader.GetString(0),
                    ClientSlug: reader.GetString(1),
                    Slug: reader.GetString(2),
                    DisplayName: reader.GetString(3),
                    CreatedAt: ParseTs(reader.GetString(4)) ?? DateTimeOffset.UtcNow,
                    ArchivedAt: reader.IsDBNull(5) ? null : ParseTs(reader.GetString(5))));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCAppRecord?> GetAppAsync(string clientSlug, string appSlug, CancellationToken ct)
    {
        var normalized = RSCCatalogSlug.Normalize(appSlug);
        var apps = await ListAppsAsync(clientSlug, includeArchived: true, ct).ConfigureAwait(false);
        return apps.FirstOrDefault(a => string.Equals(a.Slug, normalized, StringComparison.Ordinal));
    }

    public async Task<RSCAppRecord> CreateAppAsync(
        string clientSlug, string appSlug, string displayName, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Require(clientSlug, "Client slug");
        if (!IsValidClient(normClient))
            throw new RSCCatalogException($"Unknown client '{normClient}'. Register the client before adding apps to it.");
        var normSlug = RSCCatalogSlug.Require(appSlug, "App slug");
        var name = string.IsNullOrWhiteSpace(displayName) ? normSlug : displayName.Trim();

        var id = NewId("app");
        var createdAt = DateTimeOffset.UtcNow;

        try
        {
            await ExecuteWithRetryAsync(async innerCt =>
            {
                using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
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
                return 0;
            }, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsConstraint(ex))
        {
            throw new RSCCatalogException($"Client '{normClient}' already has an app with slug '{normSlug}'.");
        }

        RebuildCacheSafe();
        return new RSCAppRecord(id, normClient, normSlug, name, createdAt, null);
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

    public async Task<bool> ArchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        RequireNotDefaultApp(normClient, normSlug, "archived");
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

    public async Task<bool> UnarchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        var restored = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE apps SET archived_at = NULL WHERE client_id = @client AND slug = @slug AND archived_at IS NOT NULL;";
            cmd.Parameters.AddWithValue("@client", normClient);
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
        if (restored) RebuildCacheSafe();
        return restored;
    }

    public async Task<bool> DeleteAppAsync(string clientSlug, string appSlug, CancellationToken ct)
    {
        EnsureReady();
        var normClient = RSCCatalogSlug.Normalize(clientSlug);
        var normSlug = RSCCatalogSlug.Normalize(appSlug);
        RequireNotDefaultApp(normClient, normSlug, "deleted");
        var deleted = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM apps WHERE client_id = @client AND slug = @slug;";
            cmd.Parameters.AddWithValue("@client", normClient);
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
        if (deleted) RebuildCacheSafe();
        return deleted;
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
        RequireNotDefaultClient(normSlug, "archived");
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

    public async Task<bool> UnarchiveClientAsync(string slug, CancellationToken ct)
    {
        EnsureReady();
        var normSlug = RSCCatalogSlug.Normalize(slug);
        var restored = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clients SET archived_at = NULL WHERE slug = @slug AND archived_at IS NOT NULL;";
            cmd.Parameters.AddWithValue("@slug", normSlug);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
        // Cache rebuild matters here: the client (and so its non-archived apps) re-enter the validation
        // snapshot and the dashboards.
        if (restored) RebuildCacheSafe();
        return restored;
    }

    public async Task<bool> DeleteClientAsync(string slug, CancellationToken ct)
    {
        EnsureReady();
        var normSlug = RSCCatalogSlug.Normalize(slug);
        RequireNotDefaultClient(normSlug, "deleted");
        var deleted = await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(innerCt).ConfigureAwait(false);
            // Remove the client's apps first, then the client itself, atomically.
            using (var apps = conn.CreateCommand())
            {
                apps.Transaction = tx;
                apps.CommandTimeout = _commandTimeoutSeconds;
                apps.CommandText = "DELETE FROM apps WHERE client_id = @slug;";
                apps.Parameters.AddWithValue("@slug", normSlug);
                await apps.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            int rows;
            using (var cli = conn.CreateCommand())
            {
                cli.Transaction = tx;
                cli.CommandTimeout = _commandTimeoutSeconds;
                cli.CommandText = "DELETE FROM clients WHERE slug = @slug;";
                cli.Parameters.AddWithValue("@slug", normSlug);
                rows = await cli.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }
            await tx.CommitAsync(innerCt).ConfigureAwait(false);
            return rows > 0;
        }, ct).ConfigureAwait(false);
        if (deleted) RebuildCacheSafe();
        return deleted;
    }

    // Active apps require an active owning client too (same rule as the validation snapshot), so the
    // Status count doesn't include apps hidden behind an archived client.
    public Task<int> CountActiveAppsAsync(CancellationToken ct) => CountAsync(
        "SELECT COUNT(*) FROM apps a JOIN clients c ON c.slug = a.client_id WHERE a.archived_at IS NULL AND c.archived_at IS NULL;", ct);
    public Task<int> CountActiveClientsAsync(CancellationToken ct) => CountAsync(
        "SELECT COUNT(*) FROM clients WHERE archived_at IS NULL;", ct);

    private async Task<int> CountAsync(string sql, CancellationToken ct)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = await OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }
        catch { return 0; }
    }

    // -------- Default-tenant guards --------

    private void RequireNotDefaultClient(string normSlug, string verb)
    {
        if (string.Equals(normSlug, _defaultClient, StringComparison.Ordinal))
            throw new RSCCatalogException($"The default client '{normSlug}' cannot be {verb} — it is the fallback for attribution-omitting traffic.");
    }

    private void RequireNotDefaultApp(string normClient, string normApp, string verb)
    {
        if (string.Equals(normClient, _defaultClient, StringComparison.Ordinal) &&
            string.Equals(normApp, _defaultApp, StringComparison.Ordinal))
            throw new RSCCatalogException($"The default app '{normClient}/{normApp}' cannot be {verb} — it is the fallback for attribution-omitting traffic.");
    }

    private static string DefaultOrFallback(string? slug)
    {
        var n = RSCCatalogSlug.Normalize(slug);
        return n.Length == 0 ? "default" : n;
    }

    // -------- Bootstrap seeding --------

    private void SeedDefaultsAndOptions(SqliteConnection conn, RSCCatalogOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        var defaultClient = RSCCatalogSlug.Normalize(options.DefaultClientSlug);

        // Always-present default client + its default app so attribution-omitting (older) SDK
        // builds, and any key with no client binding, still resolve.
        SeedClientIfMissing(conn, defaultClient, "Default client", now);
        SeedAppIfMissing(conn, defaultClient, RSCCatalogSlug.Normalize(options.DefaultAppSlug), "Default app", now);

        // Config-declared client seeds first (dev appsettings supplies demo clients).
        foreach (var client in options.SeedClients)
        {
            var slug = RSCCatalogSlug.Normalize(client.Slug);
            if (!RSCCatalogSlug.IsValid(slug)) continue;
            SeedClientIfMissing(conn, slug, string.IsNullOrWhiteSpace(client.DisplayName) ? slug : client.DisplayName.Trim(), now);
        }

        // Config-declared app seeds, each owned by a client. Apps whose ClientSlug is blank or
        // unregistered fall back to the default client. (Environment, if any, is part of the slug.)
        foreach (var app in options.SeedApps)
        {
            var slug = RSCCatalogSlug.Normalize(app.Slug);
            if (!RSCCatalogSlug.IsValid(slug)) continue;
            var owner = RSCCatalogSlug.Normalize(app.ClientSlug);
            if (!RSCCatalogSlug.IsValid(owner) || !ClientExists(conn, owner)) owner = defaultClient;
            SeedAppIfMissing(conn, owner, slug, string.IsNullOrWhiteSpace(app.DisplayName) ? slug : app.DisplayName.Trim(), now);
        }
    }

    private static bool ClientExists(SqliteConnection conn, string slug)
    {
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT 1 FROM clients WHERE slug = @slug;";
        find.Parameters.AddWithValue("@slug", slug);
        return find.ExecuteScalar() is not null;
    }

    private void SeedAppIfMissing(SqliteConnection conn, string clientSlug, string slug, string displayName, DateTimeOffset now)
    {
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT 1 FROM apps WHERE client_id = @client AND slug = @slug;";
        find.Parameters.AddWithValue("@client", clientSlug);
        find.Parameters.AddWithValue("@slug", slug);
        if (find.ExecuteScalar() is not null) return;
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO apps(id, client_id, slug, display_name, created_at, archived_at) VALUES(@id,@client,@slug,@name,@at,NULL);";
        ins.Parameters.AddWithValue("@id", NewId("app"));
        ins.Parameters.AddWithValue("@client", clientSlug);
        ins.Parameters.AddWithValue("@slug", slug);
        ins.Parameters.AddWithValue("@name", displayName);
        ins.Parameters.AddWithValue("@at", Format(now));
        ins.ExecuteNonQuery();
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
            _health.MarkHealthy(Component, $"{_apps.Count} apps, {_clientSlugs.Count} clients cached");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog cache reload failed; serving previous snapshot");
            _health.MarkDegraded(Component, $"cache reload failed: {ex.Message}", ex);
        }
    }

    private void RebuildCache(SqliteConnection conn)
    {
        var apps = new HashSet<(string, string)>();
        using (var cmd = conn.CreateCommand())
        {
            // An app validates only while BOTH it and its owning client are active — archiving a client
            // therefore disables ingestion to all its apps without touching the app rows (so un-archiving
            // restores them exactly).
            cmd.CommandText = @"
SELECT a.client_id, a.slug
FROM apps a JOIN clients c ON c.slug = a.client_id
WHERE a.archived_at IS NULL AND c.archived_at IS NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                apps.Add((reader.GetString(0), reader.GetString(1)));
        }

        var clients = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM clients WHERE archived_at IS NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) clients.Add(reader.GetString(0));
        }

        _apps = apps;
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
