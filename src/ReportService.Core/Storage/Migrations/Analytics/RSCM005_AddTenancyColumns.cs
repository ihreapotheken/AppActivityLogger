using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Analytics;

/// <summary>
/// v5 of the analytics schema. Adds the <c>(app_id, environment, client_id)</c> tenancy dimension
/// alongside the existing <c>platform</c>, threading the three columns into every table's primary
/// key / UNIQUE constraint and the indexes that filter by tenant. SQLite cannot alter a primary key
/// or table-level UNIQUE in place, so tables whose key changes are rebuilt
/// (CREATE _new → INSERT…SELECT → DROP → RENAME); fully-derived tables are dropped and recreated;
/// tables where the columns are merely descriptive get <c>ALTER TABLE ADD COLUMN</c>.
/// </summary>
/// <remarks>
/// Pre-existing rows backfill to the literals <c>app_id='default'</c>, <c>client_id='default'</c>,
/// <c>environment='production'</c> — the same defaults <c>RSCSqliteCatalog</c> self-seeds and the
/// ingestion layer stamps on attribution-omitting batches, so backfilled history and new
/// default-attributed traffic land in the same tenant. The whole migration runs once inside the
/// schema runner's transaction; the ColumnExists guard keeps a re-run (after a rolled-back partial
/// apply) clean.
/// </remarks>
internal sealed class RSCM005_AddTenancyColumns : RSCISchemaMigration
{
    public int Version => 5;
    public string Description => "add (app_id, environment, client_id) tenancy columns to all analytics tables";

    private const string DefaultApp = "default";
    private const string DefaultClient = "default";
    private const string DefaultEnv = "production";

    public void Up(SqliteConnection conn)
    {
        // analytics_events already carries the columns ⇒ this migration already ran (re-entry after a
        // rolled-back partial). The whole Up is transactional, so the guard is belt-and-braces.
        if (RSCMigrationHelpers.ColumnExists(conn, "analytics_events", "app_id"))
            return;

        // ---- analytics_events: REBUILD (UNIQUE(platform,event_id) → UNIQUE(app,env,client,platform,event_id)) ----
        RSCMigrationHelpers.Execute(conn, $@"
CREATE TABLE analytics_events_new (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  event_id           TEXT NOT NULL,
  batch_id           TEXT NOT NULL,
  app_id             TEXT NOT NULL,
  environment        TEXT NOT NULL,
  client_id          TEXT NOT NULL,
  platform           TEXT NOT NULL,
  session_id         TEXT NOT NULL,
  anonymous_id_hash  TEXT,
  client_id_hash     TEXT,
  hash_version       INTEGER NOT NULL,
  occurred_at        TEXT NOT NULL,
  received_at        TEXT NOT NULL,
  sequence           INTEGER NOT NULL,
  type               TEXT NOT NULL,
  name               TEXT NOT NULL,
  screen             TEXT,
  feature            TEXT,
  duration_ms        INTEGER,
  properties_json    TEXT NOT NULL,
  items_json         TEXT NOT NULL,
  aggregated_at      TEXT,
  UNIQUE(app_id, environment, client_id, platform, event_id)
);
INSERT INTO analytics_events_new
  (id, event_id, batch_id, app_id, environment, client_id, platform, session_id, anonymous_id_hash,
   client_id_hash, hash_version, occurred_at, received_at, sequence, type, name, screen, feature,
   duration_ms, properties_json, items_json, aggregated_at)
SELECT
   id, event_id, batch_id, '{DefaultApp}', '{DefaultEnv}', '{DefaultClient}', platform, session_id, anonymous_id_hash,
   client_id_hash, hash_version, occurred_at, received_at, sequence, type, name, screen, feature,
   duration_ms, properties_json, items_json, aggregated_at
FROM analytics_events;
DROP TABLE analytics_events;
ALTER TABLE analytics_events_new RENAME TO analytics_events;
CREATE INDEX IF NOT EXISTS idx_analytics_events_occurred
  ON analytics_events(occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_events_session
  ON analytics_events(session_id, sequence);
CREATE INDEX IF NOT EXISTS idx_analytics_events_aggregated
  ON analytics_events(aggregated_at) WHERE aggregated_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_analytics_events_type_name
  ON analytics_events(type, name);
CREATE INDEX IF NOT EXISTS idx_analytics_events_scope_occurred
  ON analytics_events(app_id, environment, client_id, platform, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_events_scope_event_id
  ON analytics_events(app_id, environment, client_id, platform, event_id);");

        // ---- analytics_sessions: REBUILD (PK gains app/env/client) ----
        RSCMigrationHelpers.Execute(conn, $@"
CREATE TABLE analytics_sessions_new (
  app_id             TEXT NOT NULL,
  environment        TEXT NOT NULL,
  client_id          TEXT NOT NULL,
  platform           TEXT NOT NULL,
  session_id         TEXT NOT NULL,
  anonymous_id_hash  TEXT,
  started_at         TEXT NOT NULL,
  last_seen_at       TEXT NOT NULL,
  event_count        INTEGER NOT NULL DEFAULT 0,
  screen_count       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, environment, client_id, platform, session_id)
);
INSERT INTO analytics_sessions_new
  (app_id, environment, client_id, platform, session_id, anonymous_id_hash, started_at, last_seen_at, event_count, screen_count)
SELECT '{DefaultApp}', '{DefaultEnv}', '{DefaultClient}', platform, session_id, anonymous_id_hash, started_at, last_seen_at, event_count, screen_count
FROM analytics_sessions;
DROP TABLE analytics_sessions;
ALTER TABLE analytics_sessions_new RENAME TO analytics_sessions;
CREATE INDEX IF NOT EXISTS idx_analytics_sessions_started
  ON analytics_sessions(started_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_sessions_scope_seen
  ON analytics_sessions(app_id, environment, client_id, platform, last_seen_at DESC);");

        // ---- analytics_user_days: REBUILD (PK gains app/env/client) ----
        RSCMigrationHelpers.Execute(conn, $@"
CREATE TABLE analytics_user_days_new (
  app_id             TEXT NOT NULL,
  environment        TEXT NOT NULL,
  client_id          TEXT NOT NULL,
  platform           TEXT NOT NULL,
  day                TEXT NOT NULL,
  anonymous_id_hash  TEXT NOT NULL,
  hash_version       INTEGER NOT NULL,
  events             INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, environment, client_id, platform, day, anonymous_id_hash)
);
INSERT INTO analytics_user_days_new
  (app_id, environment, client_id, platform, day, anonymous_id_hash, hash_version, events)
SELECT '{DefaultApp}', '{DefaultEnv}', '{DefaultClient}', platform, day, anonymous_id_hash, hash_version, events
FROM analytics_user_days;
DROP TABLE analytics_user_days;
ALTER TABLE analytics_user_days_new RENAME TO analytics_user_days;
CREATE INDEX IF NOT EXISTS idx_analytics_user_days_day
  ON analytics_user_days(day);");

        // ---- analytics_daily_rollups: REBUILD (PK gains app/env/client) ----
        RSCMigrationHelpers.Execute(conn, $@"
CREATE TABLE analytics_daily_rollups_new (
  app_id             TEXT NOT NULL,
  environment        TEXT NOT NULL,
  client_id          TEXT NOT NULL,
  day                TEXT NOT NULL,
  platform           TEXT NOT NULL,
  events             INTEGER NOT NULL DEFAULT 0,
  sessions           INTEGER NOT NULL DEFAULT 0,
  distinct_users     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, environment, client_id, day, platform)
);
INSERT INTO analytics_daily_rollups_new
  (app_id, environment, client_id, day, platform, events, sessions, distinct_users)
SELECT '{DefaultApp}', '{DefaultEnv}', '{DefaultClient}', day, platform, events, sessions, distinct_users
FROM analytics_daily_rollups;
DROP TABLE analytics_daily_rollups;
ALTER TABLE analytics_daily_rollups_new RENAME TO analytics_daily_rollups;
CREATE INDEX IF NOT EXISTS idx_analytics_daily_rollups_day
  ON analytics_daily_rollups(day DESC);");

        // ---- analytics_funnel_steps: DROP + recreate (fully re-derived by the funnel worker) ----
        RSCMigrationHelpers.Execute(conn, @"
DROP TABLE IF EXISTS analytics_funnel_steps;
CREATE TABLE analytics_funnel_steps (
  funnel_key         TEXT NOT NULL,
  step_index         INTEGER NOT NULL,
  app_id             TEXT NOT NULL,
  environment        TEXT NOT NULL,
  client_id          TEXT NOT NULL,
  platform           TEXT NOT NULL,
  session_id         TEXT NOT NULL,
  reached_at         TEXT NOT NULL,
  PRIMARY KEY (funnel_key, app_id, environment, client_id, platform, session_id, step_index)
);
CREATE INDEX IF NOT EXISTS idx_analytics_funnel_step_reached
  ON analytics_funnel_steps(funnel_key, step_index, reached_at DESC);");

        // ---- analytics_retention_cohorts: DROP + recreate (fully re-derived by the cohort worker) ----
        RSCMigrationHelpers.Execute(conn, @"
DROP TABLE IF EXISTS analytics_retention_cohorts;
CREATE TABLE analytics_retention_cohorts (
  app_id         TEXT    NOT NULL,
  environment    TEXT    NOT NULL,
  client_id      TEXT    NOT NULL,
  platform       TEXT    NOT NULL,
  install_day    TEXT    NOT NULL,
  cohort_size    INTEGER NOT NULL,
  d1_retained    INTEGER NOT NULL DEFAULT 0,
  d7_retained    INTEGER NOT NULL DEFAULT 0,
  d30_retained   INTEGER NOT NULL DEFAULT 0,
  hash_version   INTEGER NOT NULL,
  computed_at    TEXT    NOT NULL,
  PRIMARY KEY (app_id, environment, client_id, platform, install_day)
);
CREATE INDEX IF NOT EXISTS idx_analytics_retention_cohorts_day
  ON analytics_retention_cohorts(install_day DESC);");

        // ---- analytics_batches: ADD COLUMN ×3 (PK stays batch_id) ----
        RSCMigrationHelpers.Execute(conn, $@"
ALTER TABLE analytics_batches ADD COLUMN app_id TEXT NOT NULL DEFAULT '{DefaultApp}';
ALTER TABLE analytics_batches ADD COLUMN environment TEXT NOT NULL DEFAULT '{DefaultEnv}';
ALTER TABLE analytics_batches ADD COLUMN client_id TEXT NOT NULL DEFAULT '{DefaultClient}';
CREATE INDEX IF NOT EXISTS idx_analytics_batches_scope_received
  ON analytics_batches(app_id, environment, client_id, received_at DESC);");

        // ---- analytics_dead_letters: ADD COLUMN ×3 (PK stays autoincrement id) ----
        RSCMigrationHelpers.Execute(conn, $@"
ALTER TABLE analytics_dead_letters ADD COLUMN app_id TEXT NOT NULL DEFAULT '{DefaultApp}';
ALTER TABLE analytics_dead_letters ADD COLUMN environment TEXT NOT NULL DEFAULT '{DefaultEnv}';
ALTER TABLE analytics_dead_letters ADD COLUMN client_id TEXT NOT NULL DEFAULT '{DefaultClient}';");
    }
}
