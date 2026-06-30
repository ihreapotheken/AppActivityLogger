using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Catalog;

/// <summary>
/// v1 of the <c>catalog.db</c> schema: the tenancy registry of <c>apps</c> (+ their
/// <c>app_environments</c>) and <c>clients</c>. This DB keeps its own <c>PRAGMA user_version</c>
/// ladder, so versions restart at 1 — independent of the reports <c>RSCM0xx</c> ladder. The
/// <c>MA</c> prefix (Migration, Apps/cAtalog) avoids class-name clashes with the reports
/// (<c>RSCM</c>), api-keys (<c>RSCMK</c>), and deep-link (<c>RSCDM</c>) migrations.
/// </summary>
/// <remarks>
/// <c>apps.slug</c> and <c>clients.slug</c> are <c>UNIQUE</c> and treated as immutable client-facing
/// keys (the SDK sends <c>appId</c>/<c>clientId</c> as these slugs); only <c>display_name</c> is
/// editable. <c>clients</c> is a global, parallel axis — intentionally no foreign key to
/// <c>apps</c>. There are no FKs anywhere (consistent with the rest of the codebase); the store
/// enforces logical integrity.
/// </remarks>
internal sealed class RSCMA001_CreateCatalog : RSCISchemaMigration
{
    public int Version => 1;
    public string Description => "create catalog tables (apps, app_environments, clients)";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS apps (
  id            TEXT PRIMARY KEY,
  slug          TEXT NOT NULL UNIQUE,
  display_name  TEXT NOT NULL,
  created_at    TEXT NOT NULL,
  archived_at   TEXT NULL
);

CREATE TABLE IF NOT EXISTS app_environments (
  app_id        TEXT NOT NULL,
  environment   TEXT NOT NULL,
  created_at    TEXT NOT NULL,
  PRIMARY KEY (app_id, environment)
);
CREATE INDEX IF NOT EXISTS idx_app_environments_app ON app_environments(app_id);

CREATE TABLE IF NOT EXISTS clients (
  id            TEXT PRIMARY KEY,
  slug          TEXT NOT NULL UNIQUE,
  display_name  TEXT NOT NULL,
  created_at    TEXT NOT NULL,
  archived_at   TEXT NULL
);");
    }
}
