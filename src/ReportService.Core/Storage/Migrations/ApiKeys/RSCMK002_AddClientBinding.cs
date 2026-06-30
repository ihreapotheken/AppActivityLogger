using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.ApiKeys;

/// <summary>
/// v2 of the <c>api-keys.db</c> schema. Adds <c>client_id</c> to <c>api_keys</c>: an issued key can be
/// bound to a catalog client, making the key itself the client's identity. A batch's tenancy client is
/// then derived from the authenticated key — not declared in the request body — and a client uses the
/// same key to self-manage its apps via the JSON API.
/// </summary>
/// <remarks>
/// Nullable: the static config root key and any pre-existing managed keys carry no client binding
/// (they resolve to the default-client fallback on the ingestion path, exactly as before). A plain
/// <c>ALTER TABLE ADD COLUMN</c> suffices — no key change — so the guard is just ColumnExists.
/// </remarks>
internal sealed class RSCMK002_AddClientBinding : RSCISchemaMigration
{
    public int Version => 2;
    public string Description => "add client_id binding to api_keys";

    public void Up(SqliteConnection conn)
    {
        if (RSCMigrationHelpers.ColumnExists(conn, "api_keys", "client_id"))
            return;

        RSCMigrationHelpers.Execute(conn, @"
ALTER TABLE api_keys ADD COLUMN client_id TEXT NULL;
CREATE INDEX IF NOT EXISTS idx_api_keys_client ON api_keys(client_id);");
    }
}
