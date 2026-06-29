using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage;
using ReportService.Storage.ApiKeys;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Unit coverage for <see cref="RSCSqliteApiKeyStore"/> against a real temp-file SQLite DB (the
/// project's no-mocks convention). Exercises create/list/revoke, the cache-backed <c>Resolve</c>,
/// expiry/revocation gating, and that only the hash — never the plaintext — is persisted.
/// </summary>
public class ApiKeyStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly RSCReportServiceOptions _opts;

    public ApiKeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rs-apikeys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "api-keys.db");
        _opts = new RSCReportServiceOptions { ReportsRoot = _dir, ApiKeysDbPath = _dbPath };
    }

    private RSCSqliteApiKeyStore NewStore() =>
        new(_opts, new RSCComponentHealth(), NullLogger<RSCSqliteApiKeyStore>.Instance);

    [Fact]
    public async Task Create_then_resolve_roundtrips_role_and_limit()
    {
        var store = NewStore();
        var created = await store.CreateAsync(RSCApiKeyRoles.User, "acme", expiresAt: null, rateLimitPerMinute: 42, createdBy: "test", default);

        var rec = store.Resolve(created.PlaintextKey);
        Assert.NotNull(rec);
        Assert.Equal(created.Metadata.Id, rec!.Id);
        Assert.Equal(RSCApiKeyRoles.User, rec.Role);
        Assert.Equal(42, rec.RateLimitPerMinute);

        var list = await store.ListAsync(default);
        Assert.Single(list);
        Assert.Equal("acme", list[0].Label);
        Assert.Equal(1, await store.CountActiveAsync(default));
    }

    [Fact]
    public async Task Resolve_is_cache_backed_without_a_new_instance()
    {
        var store = NewStore();
        var created = await store.CreateAsync(RSCApiKeyRoles.Admin, null, null, null, "test", default);
        // Same instance — proves the in-memory cache was refreshed by CreateAsync.
        Assert.NotNull(store.Resolve(created.PlaintextKey));
        Assert.Equal(RSCApiKeyRoles.Admin, store.Resolve(created.PlaintextKey)!.Role);
    }

    [Fact]
    public async Task Only_the_hash_is_persisted_never_the_plaintext()
    {
        var store = NewStore();
        var created = await store.CreateAsync(RSCApiKeyRoles.User, null, null, null, "test", default);

        using var conn = new SqliteConnection(ConnString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, key_hash, role, created_by FROM api_keys;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var rowText = string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i)?.ToString()));
        Assert.DoesNotContain(created.PlaintextKey, rowText, StringComparison.Ordinal);
        Assert.Equal(RSCApiKeyGenerator.Hash(created.PlaintextKey), reader.GetString(1));
    }

    [Fact]
    public async Task Revoke_makes_the_key_unresolvable_and_is_idempotent()
    {
        var store = NewStore();
        var created = await store.CreateAsync(RSCApiKeyRoles.User, null, null, null, "test", default);

        Assert.True(await store.RevokeAsync(created.Metadata.Id, "test", default));
        Assert.Null(store.Resolve(created.PlaintextKey));
        Assert.Equal(0, await store.CountActiveAsync(default));

        // Second revoke (already revoked) and unknown id both report false.
        Assert.False(await store.RevokeAsync(created.Metadata.Id, "test", default));
        Assert.False(await store.RevokeAsync("does-not-exist", "test", default));

        var list = await store.ListAsync(default);
        Assert.True(list[0].IsRevoked);
    }

    [Fact]
    public void Expired_key_resolves_to_null()
    {
        var seed = NewStore(); // creates the schema/table
        const string plaintext = "rsk_dead_beef_expiredsecret";
        RawInsert("expired", RSCApiKeyGenerator.Hash(plaintext), RSCApiKeyRoles.User,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1), revokedAt: null);

        // Fresh instance rebuilds its cache from the DB, including the hand-inserted row.
        var store = NewStore();
        Assert.Null(store.Resolve(plaintext));
    }

    [Fact]
    public async Task Create_rejects_invalid_role_and_past_expiry()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAsync("superuser", null, null, null, "test", default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAsync(RSCApiKeyRoles.User, null, DateTimeOffset.UtcNow.AddMinutes(-1), null, "test", default));
    }

    private string ConnString() => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    private void RawInsert(string id, string hash, string role, DateTimeOffset? expiresAt, DateTimeOffset? revokedAt)
    {
        using var conn = new SqliteConnection(ConnString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO api_keys(id, key_hash, role, label, created_at, created_by, expires_at, revoked_at, rate_limit_per_minute, last_used_at)
VALUES(@id, @h, @r, NULL, @c, 'test', @e, @rev, NULL, NULL);";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@r", role);
        cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@e", expiresAt is { } e ? e.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@rev", revokedAt is { } r ? r.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
