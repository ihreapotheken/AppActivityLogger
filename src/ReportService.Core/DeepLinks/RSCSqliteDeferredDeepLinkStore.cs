using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Migrations;
using ReportService.Storage.Migrations.DeepLinks;

namespace ReportService.DeepLinks;

/// <summary>
/// SQLite-backed implementation of <see cref="RSCIDeferredDeepLinkStore"/>. Lives in its own
/// database file (default <c>deeplinks.db</c>) so the schema evolves independently of the
/// problem-report index and the analytics store, and so the feature works under a read-only content
/// root (the DB lands under the writable <c>ReportsRoot</c>).
/// </summary>
public sealed class RSCSqliteDeferredDeepLinkStore : RSCIDeferredDeepLinkStore
{
    private const int MaxBusyRetries = 3;
    private const int InitialBusyBackoffMs = 25;
    private const int BusyTimeoutMs = 5_000;
    private const string IsoFormat = "O";
    private const string ClickRetentionKey = "click_retention_days";

    // The enabled-link set is scanned in C# on every POST /clicks capture (longest page-pattern
    // substring wins, which no SQL index can serve). Loading thousands of rows from SQLite on every
    // capture would be wasteful, so the set is cached in memory and reused. Writes invalidate it
    // immediately; a short TTL bounds staleness as a backstop (e.g. a second process editing links).
    private const long EnabledCacheTtlMs = 30_000;

    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<RSCSqliteDeferredDeepLinkStore> _logger;
    private int _schemaVersion;

    private readonly object _cacheLock = new();
    private List<RSCDeferredDeepLink>? _enabledCache;
    private long _enabledCacheTick;

    public string DbPath => _dbPath;
    public int SchemaVersion => _schemaVersion;

    public RSCSqliteDeferredDeepLinkStore(
        RSCReportServiceOptions reportOptions,
        RSCDeepLinkOptions deepLinkOptions,
        ILogger<RSCSqliteDeferredDeepLinkStore> logger)
    {
        _logger = logger;
        _commandTimeoutSeconds = Math.Max(1, reportOptions.SqliteCommandTimeoutSeconds);

        _dbPath = RSCStatePaths.Resolve(deepLinkOptions.SqliteDbPath, reportOptions.ReportsRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Bootstrap();
    }

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

            var runner = new RSCSchemaRunner(
                new RSCISchemaMigration[]
                {
                    new RSCDM001_CreateDeepLinkTables(),
                    new RSCDM002_AddClickQueryParams(),
                    new RSCDM003_AddSettingsAndLinkIndex(),
                    new RSCDM004_AddClickSignals(),
                }, _logger);
            _schemaVersion = runner.Run(conn);

            _logger.LogInformation("Deep-link store ready at {Path} (schema v{Version})", _dbPath, _schemaVersion);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to bootstrap SQLite deep-link store");
            throw;
        }
    }

    // -------- Link definitions --------

    public async Task<IReadOnlyList<RSCDeferredDeepLink>> ListLinksAsync(string? search, int limit, int offset, CancellationToken ct)
    {
        var like = BuildLikePattern(search);
        var capped = Math.Clamp(limit, 1, 1000);
        var skip = Math.Max(0, offset);
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCDeferredDeepLink>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT id, slug, name, page_pattern, redirect_url, enabled, created_at, updated_at
FROM deferred_deep_links
WHERE (@like IS NULL OR slug LIKE @like ESCAPE '\' OR name LIKE @like ESCAPE '\' OR page_pattern LIKE @like ESCAPE '\')
ORDER BY updated_at DESC, id DESC
LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("@like", (object?)like ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", capped);
            cmd.Parameters.AddWithValue("@offset", skip);

            var rows = new List<RSCDeferredDeepLink>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                rows.Add(ReadLink(reader));
            return rows;
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> CountLinksAsync(string? search, CancellationToken ct)
    {
        var like = BuildLikePattern(search);
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT COUNT(*) FROM deferred_deep_links
WHERE (@like IS NULL OR slug LIKE @like ESCAPE '\' OR name LIKE @like ESCAPE '\' OR page_pattern LIKE @like ESCAPE '\');";
            cmd.Parameters.AddWithValue("@like", (object?)like ?? DBNull.Value);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Builds a case-insensitive <c>LIKE</c> pattern from a free-text search, escaping the
    /// LIKE metacharacters so a literal <c>%</c>/<c>_</c> in the term doesn't widen the match.
    /// Returns null for a blank search (the query then matches all rows).</summary>
    private static string? BuildLikePattern(string? search)
    {
        var trimmed = search?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var escaped = trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return "%" + escaped + "%";
    }

    public async Task<RSCDeferredDeepLink?> GetLinkBySlugAsync(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return await ExecuteWithRetryAsync<RSCDeferredDeepLink?>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT id, slug, name, page_pattern, redirect_url, enabled, created_at, updated_at
FROM deferred_deep_links WHERE slug = @slug LIMIT 1;";
            cmd.Parameters.AddWithValue("@slug", slug);
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            return await reader.ReadAsync(innerCt).ConfigureAwait(false) ? ReadLink(reader) : null;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> UpsertLinkAsync(
        string slug, string name, string pagePattern, string redirectUrl, bool enabled, CancellationToken ct)
    {
        var nowIso = ToIso(DateTimeOffset.UtcNow);
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);

            bool wasPresent;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandTimeout = _commandTimeoutSeconds;
                probe.CommandText = "SELECT 1 FROM deferred_deep_links WHERE slug = @slug LIMIT 1;";
                probe.Parameters.AddWithValue("@slug", slug);
                wasPresent = (await probe.ExecuteScalarAsync(innerCt).ConfigureAwait(false)) is not null;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            // created_at is preserved on update (only set on the initial insert); updated_at always
            // re-stamps. enabled/name/page_pattern/redirect_url overwrite.
            cmd.CommandText = @"
INSERT INTO deferred_deep_links(slug, name, page_pattern, redirect_url, enabled, created_at, updated_at)
VALUES(@slug, @name, @page_pattern, @redirect_url, @enabled, @now, @now)
ON CONFLICT(slug) DO UPDATE SET
  name         = excluded.name,
  page_pattern = excluded.page_pattern,
  redirect_url = excluded.redirect_url,
  enabled      = excluded.enabled,
  updated_at   = excluded.updated_at;";
            cmd.Parameters.AddWithValue("@slug", slug);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@page_pattern", pagePattern);
            cmd.Parameters.AddWithValue("@redirect_url", redirectUrl);
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", nowIso);
            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            InvalidateEnabledCache();
            return !wasPresent;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> SetLinkEnabledAsync(string slug, bool enabled, CancellationToken ct)
    {
        var nowIso = ToIso(DateTimeOffset.UtcNow);
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "UPDATE deferred_deep_links SET enabled = @enabled, updated_at = @now WHERE slug = @slug;";
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", nowIso);
            cmd.Parameters.AddWithValue("@slug", slug);
            var n = await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            InvalidateEnabledCache();
            return n > 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteLinkAsync(string slug, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "DELETE FROM deferred_deep_links WHERE slug = @slug;";
            cmd.Parameters.AddWithValue("@slug", slug);
            var n = await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            InvalidateEnabledCache();
            return n > 0;
        }, ct).ConfigureAwait(false);
    }

    // -------- Click capture + matching --------

    public async Task<RSCDeferredDeepLinkClick> RecordClickAsync(
        string ip, string pageUrl, string? userAgent,
        IReadOnlyDictionary<string, string>? queryParams, IReadOnlyDictionary<string, string>? signals,
        DateTimeOffset at, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);

            // Resolve the matching link in C#: load the enabled definitions and pick the longest
            // page_pattern that is a case-insensitive substring of the visited URL. The set of
            // links is small and operator-managed, so an in-process scan is simpler — and more
            // flexible — than encoding "longest substring match" in SQL.
            var matched = await ResolveLinkAsync(conn, pageUrl, innerCt).ConfigureAwait(false);
            return await InsertClickAsync(conn, ip, pageUrl, userAgent, matched?.Slug, matched?.RedirectUrl, queryParams, signals, at, innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<RSCDeferredDeepLinkClick> RecordClickForLinkAsync(
        RSCDeferredDeepLink link, string ip, string pageUrl, string? userAgent,
        IReadOnlyDictionary<string, string>? queryParams, IReadOnlyDictionary<string, string>? signals,
        DateTimeOffset at, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            return await InsertClickAsync(conn, ip, pageUrl, userAgent, link.Slug, link.RedirectUrl, queryParams, signals, at, innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<RSCDeferredDeepLinkClick> InsertClickAsync(
        SqliteConnection conn, string ip, string pageUrl, string? userAgent,
        string? linkSlug, string? redirectUrl, IReadOnlyDictionary<string, string>? queryParams,
        IReadOnlyDictionary<string, string>? signals, DateTimeOffset at, CancellationToken ct)
    {
        var paramsJson = RSCDeepLinkQuery.Serialize(queryParams);
        var signalsJson = RSCDeepLinkQuery.Serialize(signals);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.CommandText = @"
INSERT INTO deferred_deep_link_clicks(ip, page_url, user_agent, link_slug, redirect_url, query_params, signals, created_at, matched_at)
VALUES(@ip, @page_url, @user_agent, @link_slug, @redirect_url, @query_params, @signals, @created_at, NULL);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@ip", ip);
        cmd.Parameters.AddWithValue("@page_url", pageUrl);
        cmd.Parameters.AddWithValue("@user_agent", (object?)userAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@link_slug", (object?)linkSlug ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@redirect_url", (object?)redirectUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@query_params", (object?)paramsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@signals", (object?)signalsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", ToIso(at));
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);

        return new RSCDeferredDeepLinkClick(
            Id: id,
            Ip: ip,
            PageUrl: pageUrl,
            UserAgent: userAgent,
            LinkSlug: linkSlug,
            RedirectUrl: redirectUrl,
            CreatedAt: at,
            MatchedAt: null,
            QueryParams: queryParams is { Count: > 0 } ? queryParams : null,
            Signals: signals is { Count: > 0 } ? signals : null);
    }

    public async Task<RSCDeferredDeepLinkMatch?> FindMatchForIpAsync(
        string ip, TimeSpan window, bool claim, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var cutoffIso = ToIso(now - window);
        return await ExecuteWithRetryAsync<RSCDeferredDeepLinkMatch?>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);

            // Newest unclaimed click for this IP inside the window whose resolved link still exists
            // AND is still enabled. Joining the live link row means disabling a link immediately
            // stops it matching, and an edited redirect/name takes effect for pending clicks too.
            long clickId;
            RSCDeferredDeepLinkMatch match;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT c.id, l.slug, l.name, l.redirect_url, c.page_url, c.created_at, c.query_params, c.signals
FROM deferred_deep_link_clicks c
JOIN deferred_deep_links l ON l.slug = c.link_slug AND l.enabled = 1
WHERE c.ip = @ip AND c.matched_at IS NULL AND c.created_at >= @cutoff
ORDER BY c.created_at DESC
LIMIT 1;";
                cmd.Parameters.AddWithValue("@ip", ip);
                cmd.Parameters.AddWithValue("@cutoff", cutoffIso);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (!await reader.ReadAsync(innerCt).ConfigureAwait(false)) return null;

                clickId = reader.GetInt64(0);
                match = new RSCDeferredDeepLinkMatch(
                    Slug: reader.GetString(1),
                    Name: reader.GetString(2),
                    RedirectUrl: reader.GetString(3),
                    PageUrl: reader.GetString(4),
                    ClickedAt: ParseIso(reader.GetString(5)),
                    QueryParams: reader.IsDBNull(6) ? null : RSCDeepLinkQuery.Deserialize(reader.GetString(6)),
                    Signals: reader.IsDBNull(7) ? null : RSCDeepLinkQuery.Deserialize(reader.GetString(7)));
            }

            if (claim)
            {
                using var upd = conn.CreateCommand();
                upd.CommandTimeout = _commandTimeoutSeconds;
                upd.CommandText = "UPDATE deferred_deep_link_clicks SET matched_at = @now WHERE id = @id;";
                upd.Parameters.AddWithValue("@now", ToIso(now));
                upd.Parameters.AddWithValue("@id", clickId);
                await upd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            }

            return match;
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RSCDeferredDeepLinkClick>> ListRecentClicksAsync(int limit, CancellationToken ct) =>
        ListClicksAsync(new RSCDeepLinkClickFilter(), limit, ct);

    public async Task<IReadOnlyList<RSCDeferredDeepLinkClick>> ListClicksAsync(
        RSCDeepLinkClickFilter filter, int limit, CancellationToken ct)
    {
        var capped = Math.Clamp(limit, 1, 1000);

        // Build the WHERE from the captured "header data". String filters are case-insensitive
        // substring matches; the free-text Header filter spans the User-Agent + the signals JSON
        // (the X-DeepLink-* / client-hint headers) so an operator can search by a header key or value.
        var clauses = new List<string>();
        var binders = new List<Action<SqliteCommand>>();
        void Like(string sql, string param, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            clauses.Add(sql);
            var pattern = "%" + value.Trim() + "%";
            binders.Add(c => c.Parameters.AddWithValue(param, pattern));
        }
        Like("ip LIKE @ip", "@ip", filter.Ip);
        Like("user_agent LIKE @ua", "@ua", filter.UserAgent);
        Like("(COALESCE(user_agent,'') LIKE @hdr OR COALESCE(signals,'') LIKE @hdr)", "@hdr", filter.Header);
        // "Matched" = the visit resolved to a configured link (link_slug denormalised on capture),
        // mirroring the admin "Matched link" column — NOT whether an app later claimed it (matched_at).
        if (filter.Matched is { } m)
            clauses.Add(m ? "link_slug IS NOT NULL" : "link_slug IS NULL");

        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCDeferredDeepLinkClick>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = $@"
SELECT id, ip, page_url, user_agent, link_slug, redirect_url, created_at, matched_at, query_params, signals
FROM deferred_deep_link_clicks
{where}
ORDER BY created_at DESC, id DESC
LIMIT @limit;";
            cmd.Parameters.AddWithValue("@limit", capped);
            foreach (var bind in binders) bind(cmd);

            var rows = new List<RSCDeferredDeepLinkClick>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                rows.Add(new RSCDeferredDeepLinkClick(
                    Id: reader.GetInt64(0),
                    Ip: reader.GetString(1),
                    PageUrl: reader.GetString(2),
                    UserAgent: reader.IsDBNull(3) ? null : reader.GetString(3),
                    LinkSlug: reader.IsDBNull(4) ? null : reader.GetString(4),
                    RedirectUrl: reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedAt: ParseIso(reader.GetString(6)),
                    MatchedAt: reader.IsDBNull(7) ? null : ParseIso(reader.GetString(7)),
                    QueryParams: reader.IsDBNull(8) ? null : RSCDeepLinkQuery.Deserialize(reader.GetString(8)),
                    Signals: reader.IsDBNull(9) ? null : RSCDeepLinkQuery.Deserialize(reader.GetString(9))));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Picks the enabled link whose <c>page_pattern</c> is the longest case-insensitive
    /// substring of <paramref name="pageUrl"/>. Returns null when no enabled link matches. Scans an
    /// in-memory cache of the enabled set so a high-volume capture stream doesn't reload thousands of
    /// definitions from SQLite on every click.</summary>
    private async Task<RSCDeferredDeepLink?> ResolveLinkAsync(SqliteConnection conn, string pageUrl, CancellationToken ct)
    {
        var enabled = GetCachedEnabled() ?? await LoadAndCacheEnabledAsync(conn, ct).ConfigureAwait(false);

        RSCDeferredDeepLink? best = null;
        foreach (var link in enabled)
        {
            if (string.IsNullOrEmpty(link.PagePattern)) continue;
            if (pageUrl.Contains(link.PagePattern, StringComparison.OrdinalIgnoreCase) &&
                (best is null || link.PagePattern.Length > best.PagePattern.Length))
            {
                best = link;
            }
        }
        return best;
    }

    private List<RSCDeferredDeepLink>? GetCachedEnabled()
    {
        lock (_cacheLock)
        {
            if (_enabledCache is not null && Environment.TickCount64 - _enabledCacheTick < EnabledCacheTtlMs)
                return _enabledCache;
            return null;
        }
    }

    private async Task<List<RSCDeferredDeepLink>> LoadAndCacheEnabledAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.CommandText = @"
SELECT id, slug, name, page_pattern, redirect_url, enabled, created_at, updated_at
FROM deferred_deep_links WHERE enabled = 1;";

        var list = new List<RSCDeferredDeepLink>();
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(ReadLink(reader));
        }
        lock (_cacheLock)
        {
            _enabledCache = list;
            _enabledCacheTick = Environment.TickCount64;
        }
        return list;
    }

    private void InvalidateEnabledCache()
    {
        lock (_cacheLock) { _enabledCache = null; }
    }

    // -------- Settings + retention --------

    public async Task<int?> GetClickRetentionDaysAsync(CancellationToken ct)
    {
        return await ExecuteWithRetryAsync<int?>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "SELECT value FROM deferred_deep_link_settings WHERE key = @k LIMIT 1;";
            cmd.Parameters.AddWithValue("@k", ClickRetentionKey);
            var raw = await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) as string;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : null;
        }, ct).ConfigureAwait(false);
    }

    public async Task SetClickRetentionDaysAsync(int days, CancellationToken ct)
    {
        await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
INSERT INTO deferred_deep_link_settings(key, value) VALUES(@k, @v)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            cmd.Parameters.AddWithValue("@k", ClickRetentionKey);
            cmd.Parameters.AddWithValue("@v", days.ToString(CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeClicksOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var cutoffIso = ToIso(cutoff);
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = "DELETE FROM deferred_deep_link_clicks WHERE created_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", cutoffIso);
            return await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static RSCDeferredDeepLink ReadLink(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Slug: reader.GetString(1),
        Name: reader.GetString(2),
        PagePattern: reader.GetString(3),
        RedirectUrl: reader.GetString(4),
        Enabled: reader.GetInt64(5) != 0,
        CreatedAt: ParseIso(reader.GetString(6)),
        UpdatedAt: ParseIso(reader.GetString(7)));

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
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
                    "Deep-link store SQLite busy on attempt {Attempt}/{Max}; retrying in {DelayMs}ms",
                    attempt, MaxBusyRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex, "Deep-link store SQLite operation failed (attempt {Attempt})", attempt);
                throw;
            }
        }
    }

    private static bool IsBusy(SqliteException ex)
        => ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static string ToIso(DateTimeOffset value) =>
        value.UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
