using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Analytics;

/// <summary>
/// v6 of the analytics schema. Widens the <c>analytics_batches</c> primary key from
/// <c>(batch_id)</c> alone to the tenant+platform-scoped <c>(app_id, environment, client_id,
/// platform, batch_id)</c>, matching how <c>analytics_events</c> is already keyed (RSCM005).
/// </summary>
/// <remarks>
/// RSCM005 added the three tenancy columns to <c>analytics_batches</c> via <c>ALTER TABLE ADD
/// COLUMN</c> but deliberately left the PK as <c>batch_id</c> only. That left the envelope's
/// idempotency global rather than per-tenant: two tenants whose clients happen to emit the same
/// <c>batch_id</c> (the value is client-generated and the contract reuses it across retries) would
/// collide on the PK, and the <c>ON CONFLICT(batch_id)</c> upsert would silently fold the second
/// tenant's envelope — and its accepted/rejected counts — into the first tenant's row. The events
/// themselves are unaffected (their UNIQUE is already tenant-scoped); only the batch-summary
/// metrics (Health page batch counts, SDK-version breakdown) would be wrong.
///
/// SQLite can't alter a primary key in place, so this rebuilds the table
/// (CREATE _new → INSERT…SELECT → DROP → RENAME → recreate indexes) inside the runner's
/// transaction. Pre-existing rows carry a unique <c>batch_id</c> (it was the old PK), so the
/// INSERT…SELECT can't hit a composite-key conflict. The PrimaryKeyColumnCount guard makes a
/// re-entry after a rolled-back partial apply a no-op.
/// </remarks>
internal sealed class RSCM006_ScopeBatchesIdempotency : RSCISchemaMigration
{
    public int Version => 6;
    public string Description => "scope analytics_batches idempotency to (app, environment, client, platform, batch_id)";

    public void Up(SqliteConnection conn)
    {
        // Composite PK already in place ⇒ this migration already ran (re-entry after a rolled-back
        // partial apply). The whole Up is transactional, so the guard is belt-and-braces.
        if (RSCMigrationHelpers.PrimaryKeyColumnCount(conn, "analytics_batches") > 1)
            return;

        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE analytics_batches_new (
  batch_id            TEXT NOT NULL,
  app_id              TEXT NOT NULL,
  environment         TEXT NOT NULL,
  client_id           TEXT NOT NULL,
  platform            TEXT NOT NULL,
  received_at         TEXT NOT NULL,
  generated_at        TEXT NOT NULL,
  sdk_version         TEXT NOT NULL,
  host_app_version    TEXT,
  schema_version      INTEGER NOT NULL,
  anonymous_id_hash   TEXT,
  client_id_hash      TEXT,
  hash_version        INTEGER NOT NULL,
  accepted_count      INTEGER NOT NULL,
  rejected_count      INTEGER NOT NULL,
  batch_rejected      INTEGER NOT NULL,
  batch_reject_reason TEXT,
  PRIMARY KEY (app_id, environment, client_id, platform, batch_id)
);
INSERT INTO analytics_batches_new
  (batch_id, app_id, environment, client_id, platform, received_at, generated_at, sdk_version,
   host_app_version, schema_version, anonymous_id_hash, client_id_hash, hash_version,
   accepted_count, rejected_count, batch_rejected, batch_reject_reason)
SELECT
   batch_id, app_id, environment, client_id, platform, received_at, generated_at, sdk_version,
   host_app_version, schema_version, anonymous_id_hash, client_id_hash, hash_version,
   accepted_count, rejected_count, batch_rejected, batch_reject_reason
FROM analytics_batches;
DROP TABLE analytics_batches;
ALTER TABLE analytics_batches_new RENAME TO analytics_batches;
CREATE INDEX IF NOT EXISTS idx_analytics_batches_received
  ON analytics_batches(received_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_batches_platform_received
  ON analytics_batches(platform, received_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_batches_scope_received
  ON analytics_batches(app_id, environment, client_id, received_at DESC);");
    }
}
