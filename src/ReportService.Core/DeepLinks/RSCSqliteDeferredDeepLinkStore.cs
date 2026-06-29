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

    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<RSCSqliteDeferredDeepLinkStore> _logger;
    private int _schemaVersion;

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
                new RSCISchemaMigration[] { new RSCDM001_CreateDeepLinkTables() }, _logger);
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

    public async Task<IReadOnlyList<RSCDeferredDeepLink>> ListLinksAsync(CancellationToken ct)
    {
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCDeferredDeepLink>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT id, slug, name, page_pattern, redirect_url, enabled, created_at, updated_at
FROM deferred_deep_links
ORDER BY updated_at DESC;";

            var rows = new List<RSCDeferredDeepLink>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                rows.Add(ReadLink(reader));
            return rows;
        }, ct).ConfigureAwait(false);
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
            return (await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false)) > 0;
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
            return (await cmd.ExecuteNonQueryAsync(innerCt).ConfigureAwait(false)) > 0;
        }, ct).ConfigureAwait(false);
    }

    // -------- Click capture + matching --------

    public async Task<RSCDeferredDeepLinkClick> RecordClickAsync(
        string ip, string pageUrl, string? userAgent, DateTimeOffset at, CancellationToken ct)
    {
        var atIso = ToIso(at);
        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);

            // Resolve the matching link in C#: load the enabled definitions and pick the longest
            // page_pattern that is a case-insensitive substring of the visited URL. The set of
            // links is small and operator-managed, so an in-process scan is simpler — and more
            // flexible — than encoding "longest substring match" in SQL.
            var matched = await ResolveLinkAsync(conn, pageUrl, innerCt).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
INSERT INTO deferred_deep_link_clicks(ip, page_url, user_agent, link_slug, redirect_url, created_at, matched_at)
VALUES(@ip, @page_url, @user_agent, @link_slug, @redirect_url, @created_at, NULL);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@ip", ip);
            cmd.Parameters.AddWithValue("@page_url", pageUrl);
            cmd.Parameters.AddWithValue("@user_agent", (object?)userAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@link_slug", (object?)matched?.Slug ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@redirect_url", (object?)matched?.RedirectUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", atIso);
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0L);

            return new RSCDeferredDeepLinkClick(
                Id: id,
                Ip: ip,
                PageUrl: pageUrl,
                UserAgent: userAgent,
                LinkSlug: matched?.Slug,
                RedirectUrl: matched?.RedirectUrl,
                CreatedAt: at,
                MatchedAt: null);
        }, ct).ConfigureAwait(false);
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
SELECT c.id, l.slug, l.name, l.redirect_url, c.page_url, c.created_at
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
                    ClickedAt: ParseIso(reader.GetString(5)));
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

    public async Task<IReadOnlyList<RSCDeferredDeepLinkClick>> ListRecentClicksAsync(int limit, CancellationToken ct)
    {
        var capped = Math.Clamp(limit, 1, 1000);
        return await ExecuteWithRetryAsync<IReadOnlyList<RSCDeferredDeepLinkClick>>(async innerCt =>
        {
            using var conn = await OpenAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT id, ip, page_url, user_agent, link_slug, redirect_url, created_at, matched_at
FROM deferred_deep_link_clicks
ORDER BY created_at DESC
LIMIT @limit;";
            cmd.Parameters.AddWithValue("@limit", capped);

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
                    MatchedAt: reader.IsDBNull(7) ? null : ParseIso(reader.GetString(7))));
            }
            return rows;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Picks the enabled link whose <c>page_pattern</c> is the longest case-insensitive
    /// substring of <paramref name="pageUrl"/>. Returns null when no enabled link matches.</summary>
    private async Task<RSCDeferredDeepLink?> ResolveLinkAsync(SqliteConnection conn, string pageUrl, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.CommandText = @"
SELECT id, slug, name, page_pattern, redirect_url, enabled, created_at, updated_at
FROM deferred_deep_links WHERE enabled = 1;";

        RSCDeferredDeepLink? best = null;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var link = ReadLink(reader);
            if (string.IsNullOrEmpty(link.PagePattern)) continue;
            if (pageUrl.Contains(link.PagePattern, StringComparison.OrdinalIgnoreCase) &&
                (best is null || link.PagePattern.Length > best.PagePattern.Length))
            {
                best = link;
            }
        }
        return best;
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
