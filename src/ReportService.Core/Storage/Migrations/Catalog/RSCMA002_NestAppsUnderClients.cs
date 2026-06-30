using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Catalog;

/// <summary>
/// v2 of the <c>catalog.db</c> schema. Nests <c>apps</c> under <c>clients</c>: an app is now owned by
/// exactly one client, and its slug is unique <em>per client</em> rather than globally — two clients
/// can each have an app called <c>main</c>. The client is the top-level tenant (identified by its API
/// access key); apps are a client-owned list, each rendered as its own dashboard.
/// </summary>
/// <remarks>
/// <para>Adds <c>apps.client_id</c> (the owning client's <em>slug</em> — the same verbatim business key
/// the analytics rows store as <c>client_id</c>, so the hot-path validation cache can match on slugs
/// without a join) and swaps the global <c>UNIQUE(slug)</c> for <c>UNIQUE(client_id, slug)</c>. SQLite
/// can't alter a table-level UNIQUE in place, so the table is rebuilt
/// (CREATE _new → INSERT…SELECT → DROP → RENAME).</para>
/// <para>Pre-existing apps were a global list; they backfill to the <c>default</c> client slug — the
/// same default the catalog self-seeds and attribution falls back to. <c>app_environments</c> is keyed
/// by the synthetic <c>app_id</c> and is untouched. The ColumnExists guard makes a re-entry after a
/// rolled-back partial apply a no-op.</para>
/// </remarks>
internal sealed class RSCMA002_NestAppsUnderClients : RSCISchemaMigration
{
    public int Version => 2;
    public string Description => "nest apps under clients (apps.client_id + UNIQUE(client_id, slug))";

    // The default client slug apps backfill to. Matches RSCCatalogOptions.DefaultClientSlug and the
    // analytics RSCM005 'default' literal so backfilled apps line up with default-attributed traffic.
    private const string DefaultClient = "default";

    public void Up(SqliteConnection conn)
    {
        if (RSCMigrationHelpers.ColumnExists(conn, "apps", "client_id"))
            return;

        RSCMigrationHelpers.Execute(conn, $@"
CREATE TABLE apps_new (
  id            TEXT PRIMARY KEY,
  client_id     TEXT NOT NULL,
  slug          TEXT NOT NULL,
  display_name  TEXT NOT NULL,
  created_at    TEXT NOT NULL,
  archived_at   TEXT NULL,
  UNIQUE(client_id, slug)
);
INSERT INTO apps_new (id, client_id, slug, display_name, created_at, archived_at)
SELECT id, '{DefaultClient}', slug, display_name, created_at, archived_at FROM apps;
DROP TABLE apps;
ALTER TABLE apps_new RENAME TO apps;
CREATE INDEX IF NOT EXISTS idx_apps_client ON apps(client_id);");
    }
}
