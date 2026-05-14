using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v9: extend the <c>mappings</c> table with a <c>label</c> column so an operator can register
/// multiple ordered mappings per <c>(platform, app_version)</c>. The use case is chained
/// retrace: when both an SDK and the host app are obfuscated, the SDK ships a "consumer
/// mapping" alongside the pre-minified AAR, and the host's R8 then renames on top. Reversing a
/// crash frame requires applying the host mapping first, then the SDK's consumer mapping —
/// mirroring R8's <c>retrace --mapping-file ... --mapping-file ...</c> CLI semantics.
///
/// SQLite can't drop a primary key in place, so this migration rebuilds the table from scratch:
/// existing rows are migrated under the default label <c>"host"</c>, preserving their previous
/// behaviour as the "single mapping per version" registry.
/// </summary>
internal sealed class RSCM009_MappingsAddLabel : RSCISchemaMigration
{
    public int Version => 9;
    public string Description => "add label column to mappings; composite PK (platform, app_version, label)";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS mappings_v2 (
  platform     TEXT NOT NULL,
  app_version  TEXT NOT NULL,
  label        TEXT NOT NULL DEFAULT 'host',
  uploaded_at  TEXT NOT NULL,
  size_bytes   INTEGER NOT NULL,
  sha256       TEXT NOT NULL,
  class_count  INTEGER NOT NULL,
  notes        TEXT NULL,
  ord          INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (platform, app_version, label)
);

INSERT OR IGNORE INTO mappings_v2(platform, app_version, label, uploaded_at, size_bytes, sha256, class_count, notes, ord)
SELECT platform, app_version, 'host', uploaded_at, size_bytes, sha256, class_count, notes, 0
FROM mappings;

DROP TABLE mappings;
ALTER TABLE mappings_v2 RENAME TO mappings;

CREATE INDEX IF NOT EXISTS idx_mappings_uploaded
  ON mappings(uploaded_at DESC);
CREATE INDEX IF NOT EXISTS idx_mappings_pv_ord
  ON mappings(platform, app_version, ord);
");
    }
}
