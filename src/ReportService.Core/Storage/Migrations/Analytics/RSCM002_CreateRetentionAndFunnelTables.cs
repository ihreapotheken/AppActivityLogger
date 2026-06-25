using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Analytics;

/// <summary>
/// v2 of the analytics schema. Adds the per-cohort retention table (<c>analytics_retention_cohorts</c>)
/// and the funnel definition table (<c>analytics_funnel_definitions</c>) so the dedicated cohort
/// and funnel workers have a place to write their materialised results without touching the raw
/// event stream.
/// </summary>
internal sealed class RSCM002_CreateRetentionAndFunnelTables : RSCISchemaMigration
{
    public int Version => 2;
    public string Description => "add analytics_retention_cohorts + analytics_funnel_definitions";

    public void Up(SqliteConnection conn)
    {
        // analytics_retention_cohorts: one row per (platform, install_day). Re-computed by the
        // cohort worker on a fixed cadence; the row is upserted on every recompute, so a long
        // tail of rotting cohorts never accumulates. install_day stores the day the cohort users
        // were first seen under the active hash version.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_retention_cohorts (
  platform       TEXT    NOT NULL,
  install_day    TEXT    NOT NULL,
  cohort_size    INTEGER NOT NULL,
  d1_retained    INTEGER NOT NULL DEFAULT 0,
  d7_retained    INTEGER NOT NULL DEFAULT 0,
  d30_retained   INTEGER NOT NULL DEFAULT 0,
  hash_version   INTEGER NOT NULL,
  computed_at    TEXT    NOT NULL,
  PRIMARY KEY (platform, install_day)
);
CREATE INDEX IF NOT EXISTS idx_analytics_retention_cohorts_day
  ON analytics_retention_cohorts(install_day DESC);");

        // analytics_funnel_definitions: JSON-stored definition for each named funnel. The
        // funnel worker reads from here, scans analytics_events for matching step sequences,
        // and writes per-session step observations into analytics_funnel_steps (already in v1).
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS analytics_funnel_definitions (
  funnel_key   TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  steps_json   TEXT NOT NULL,
  enabled      INTEGER NOT NULL DEFAULT 1,
  created_at   TEXT NOT NULL,
  updated_at   TEXT NOT NULL
);");
    }
}
