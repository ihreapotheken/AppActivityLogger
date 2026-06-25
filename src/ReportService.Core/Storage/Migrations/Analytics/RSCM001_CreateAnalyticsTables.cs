using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Analytics;

/// <summary>
/// v1 of the analytics schema. One migration creates every table the v2 pipeline needs — splitting
/// into smaller migrations only pays off once we're evolving columns on a live deployment, which
/// hasn't happened yet for this schema.
/// </summary>
internal sealed class RSCM001_CreateAnalyticsTables : RSCISchemaMigration
{
    public int Version => 1;
    public string Description => "create v2 analytics tables (batches, events, sessions, user_days, daily_rollups, funnel_steps, dead_letters)";

    public void Up(SqliteConnection conn)
    {
        // analytics_batches: one row per ingested batch envelope. Captures the SDK identity and
        // arrival metadata. UNIQUE(batch_id) keeps a retried batch idempotent at the envelope level.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_batches (
  batch_id           TEXT PRIMARY KEY,
  received_at        TEXT NOT NULL,
  generated_at       TEXT NOT NULL,
  platform           TEXT NOT NULL,
  sdk_version        TEXT NOT NULL,
  host_app_version   TEXT,
  schema_version     INTEGER NOT NULL,
  anonymous_id_hash  TEXT,
  client_id_hash     TEXT,
  hash_version       INTEGER NOT NULL,
  accepted_count     INTEGER NOT NULL,
  rejected_count     INTEGER NOT NULL,
  batch_rejected     INTEGER NOT NULL,
  batch_reject_reason TEXT
);
CREATE INDEX IF NOT EXISTS idx_analytics_batches_received
  ON analytics_batches(received_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_batches_platform_received
  ON analytics_batches(platform, received_at DESC);");

        // analytics_events: one row per accepted event. UNIQUE(platform, event_id) is the
        // server-side idempotency guarantee — a retry that reuses an event_id is a no-op.
        // aggregated_at is null until the aggregation worker has folded the row into the rollup
        // tables; the worker scans the null pool on every tick.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_events (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  event_id           TEXT NOT NULL,
  batch_id           TEXT NOT NULL,
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
  UNIQUE(platform, event_id)
);
CREATE INDEX IF NOT EXISTS idx_analytics_events_occurred
  ON analytics_events(occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_events_session
  ON analytics_events(session_id, sequence);
CREATE INDEX IF NOT EXISTS idx_analytics_events_aggregated
  ON analytics_events(aggregated_at) WHERE aggregated_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_analytics_events_type_name
  ON analytics_events(type, name);
CREATE INDEX IF NOT EXISTS idx_analytics_events_platform_occurred
  ON analytics_events(platform, occurred_at DESC);");

        // analytics_sessions: derived from analytics_events. One row per (platform, session_id),
        // updated by the aggregation worker as new events arrive in the same session.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_sessions (
  platform           TEXT NOT NULL,
  session_id         TEXT NOT NULL,
  anonymous_id_hash  TEXT,
  started_at         TEXT NOT NULL,
  last_seen_at       TEXT NOT NULL,
  event_count        INTEGER NOT NULL DEFAULT 0,
  screen_count       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (platform, session_id)
);
CREATE INDEX IF NOT EXISTS idx_analytics_sessions_started
  ON analytics_sessions(started_at DESC);");

        // analytics_user_days: which hashed users were active on which day. Drives DAU/WAU/MAU
        // and retention cohorts. hash_version is stored alongside the hash so a pepper rotation
        // doesn't silently look like everyone churned overnight.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_user_days (
  platform           TEXT NOT NULL,
  day                TEXT NOT NULL,
  anonymous_id_hash  TEXT NOT NULL,
  hash_version       INTEGER NOT NULL,
  events             INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (platform, day, anonymous_id_hash)
);
CREATE INDEX IF NOT EXISTS idx_analytics_user_days_day
  ON analytics_user_days(day);");

        // analytics_daily_rollups: pre-aggregated counts for the dashboard. Avoids COUNT(DISTINCT)
        // over the full event table on every page render.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_daily_rollups (
  day                TEXT NOT NULL,
  platform           TEXT NOT NULL,
  events             INTEGER NOT NULL DEFAULT 0,
  sessions           INTEGER NOT NULL DEFAULT 0,
  distinct_users     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, platform)
);
CREATE INDEX IF NOT EXISTS idx_analytics_daily_rollups_day
  ON analytics_daily_rollups(day DESC);");

        // analytics_funnel_steps: which sessions reached which step of a named funnel. Funnels
        // are JSON-defined in admin config — this table just records observation, not the
        // definition. step_index lets us count conversion at each stage.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_funnel_steps (
  funnel_key         TEXT NOT NULL,
  step_index         INTEGER NOT NULL,
  platform           TEXT NOT NULL,
  session_id         TEXT NOT NULL,
  reached_at         TEXT NOT NULL,
  PRIMARY KEY (funnel_key, session_id, step_index)
);
CREATE INDEX IF NOT EXISTS idx_analytics_funnel_step_reached
  ON analytics_funnel_steps(funnel_key, step_index, reached_at DESC);");

        // analytics_dead_letters: events the validator wouldn't admit. Capped TTL (configurable)
        // so it stays operational-signal-sized. Surfaced on /Analytics/Health.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_dead_letters (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  received_at        TEXT NOT NULL,
  batch_id           TEXT NOT NULL,
  platform           TEXT NOT NULL,
  event_id           TEXT,
  reason             TEXT NOT NULL,
  detail             TEXT,
  raw_json           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_analytics_dead_letters_received
  ON analytics_dead_letters(received_at DESC);
CREATE INDEX IF NOT EXISTS idx_analytics_dead_letters_reason
  ON analytics_dead_letters(reason);");
    }
}
