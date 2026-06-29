using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.ApiKeys;

/// <summary>
/// v1 of the <c>api-keys.db</c> schema: the <c>api_keys</c> table backing managed API keys
/// (admin/user roles, optional expiry, revocation, optional per-key rate-limit override). This DB
/// keeps its own <c>PRAGMA user_version</c> ladder, so versions restart at 1 — independent of the
/// reports <c>RSCM0xx</c> ladder. The <c>MK</c> prefix (Migration, Keys) avoids class-name clashes
/// with the reports migrations.
/// </summary>
/// <remarks>
/// Only the SHA-256 hash of each key is stored; the plaintext is shown once at creation and never
/// persisted. <c>key_hash</c> is UNIQUE so a (vanishingly unlikely) duplicate hash can't create two
/// rows. The lookup index is the UNIQUE constraint on <c>key_hash</c> — that's the hot path for auth.
/// </remarks>
internal sealed class RSCMK001_CreateApiKeys : RSCISchemaMigration
{
    public int Version => 1;
    public string Description => "create api_keys table";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS api_keys (
  id                     TEXT PRIMARY KEY,
  key_hash               TEXT NOT NULL UNIQUE,
  role                   TEXT NOT NULL,
  label                  TEXT NULL,
  created_at             TEXT NOT NULL,
  created_by             TEXT NULL,
  expires_at             TEXT NULL,
  revoked_at             TEXT NULL,
  rate_limit_per_minute  INTEGER NULL,
  last_used_at           TEXT NULL
);");
    }
}
