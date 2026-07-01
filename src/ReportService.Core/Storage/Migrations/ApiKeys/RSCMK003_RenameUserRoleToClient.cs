using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.ApiKeys;

/// <summary>
/// v3 of the <c>api-keys.db</c> schema. Collapses the old three-way key space (admin / bound-user /
/// unbound-user) into the two-role model: <c>admin</c> (unbound, all clients) and <c>client</c>
/// (bound, one client).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>A bound <c>user</c> key already <i>was</i> a client's identity — it simply becomes a
///         <c>client</c> key (a pure role rename, data unchanged).</item>
///   <item>An <b>unbound <c>user</c> key</b> is a "legacy" key with no seat in the new model (it can't
///         be a client key — nothing to bind — and it isn't an admin). Rather than silently promoting
///         it, we <b>revoke</b> it so it stops authenticating; an operator can mint a replacement
///         (admin, or a bound client key) deliberately. The row is kept for the audit trail.</item>
/// </list>
/// Idempotent: after the first run there are no <c>role='user'</c> rows left, so a rerun is a no-op.
/// The revoke timestamp uses the same ISO-8601 shape the store writes/parses (see
/// <c>RSCSqliteApiKeyStore.Format</c> / <c>ParseTs</c>).
/// </remarks>
internal sealed class RSCMK003_RenameUserRoleToClient : RSCISchemaMigration
{
    public int Version => 3;
    public string Description => "collapse api_keys roles to admin/client (rename bound user→client, revoke unbound legacy user keys)";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
-- A bound 'user' key is already a client's identity: rename it to the 'client' role.
UPDATE api_keys SET role = 'client' WHERE role = 'user' AND client_id IS NOT NULL;

-- Any remaining 'user' key is unbound and has no place in the two-role model: revoke it
-- (kept for audit; stops authenticating). Only touch rows not already revoked.
UPDATE api_keys
   SET revoked_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
 WHERE role = 'user' AND revoked_at IS NULL;");
    }
}
