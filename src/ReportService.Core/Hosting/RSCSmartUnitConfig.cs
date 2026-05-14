using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ReportService.Hosting;

/// <summary>
/// Operator-facing env-var façade for <see cref="ReportService.Options.RSCReportServiceOptions"/>
/// and the admin options. The canonical config keys use the .NET convention
/// (e.g. <c>ReportService__RetentionMaxBytes</c>, in bytes) which is precise but unfriendly to
/// edit in a <c>.env</c> file — every byte cap ends up as a 10-digit number. This helper reads a
/// short, capitalised set of operator-facing variables in human units (MB for storage caps,
/// minutes for intervals, etc.), converts them, and surfaces them as the canonical config keys
/// via an in-memory provider added AFTER the default providers so the smart-unit value wins.
///
/// Setting the canonical <c>ReportService__*</c> form directly still works — it just gets
/// overridden by the smart-unit value when both are present. Tests that need byte-exact control
/// continue to set <see cref="ReportService.Options.RSCReportServiceOptions"/> properties on the
/// in-memory singleton without going through this layer.
/// </summary>
public static class RSCSmartUnitConfig
{
    private const long Kib = 1024L;
    private const long Mib = 1024L * 1024;

    /// <summary>Mounts the translation provider onto the supplied builder.</summary>
    public static void Apply(IConfigurationBuilder config)
    {
        var translated = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Storage caps + retention — the headline knobs an operator actually reaches for.
        Map(translated, "REPORT_RETENTION_ENABLED",          "ReportService:RetentionEnabled");
        MapScaled(translated, "REPORT_RETENTION_MAX_MB",     "ReportService:RetentionMaxBytes",     Mib);
        Map(translated, "REPORT_RETENTION_MAX_DAYS",         "ReportService:RetentionMaxAgeDays");
        MapScaled(translated, "REPORT_RETENTION_SCAN_INTERVAL_MINUTES",
                                                             "ReportService:RetentionScanIntervalSeconds", 60);

        // Payload size caps.
        MapScaled(translated, "REPORT_MAX_UPLOAD_MB",        "ReportService:MaxUploadBytes",        Mib);
        MapScaled(translated, "REPORT_MAX_ATTACHMENT_MB",    "ReportService:MaxAttachmentBytes",    Mib);
        MapScaled(translated, "REPORT_MAX_JSON_KB",          "ReportService:MaxJsonBytes",          Kib);

        // Ingestion shaping.
        Map(translated, "REPORT_INGEST_CONCURRENCY",         "ReportService:IngestConcurrency");
        Map(translated, "REPORT_INGEST_QUEUE_LIMIT",         "ReportService:IngestQueueLimit");
        Map(translated, "REPORT_INGEST_TIMEOUT_SECONDS",     "ReportService:IngestTimeoutSeconds");
        Map(translated, "REPORT_RATE_LIMIT_PER_MINUTE",      "ReportService:RateLimitPermitsPerMinute");
        Map(translated, "REPORT_SQLITE_COMMAND_TIMEOUT_SECONDS",
                                                             "ReportService:SqliteCommandTimeoutSeconds");

        // Auth abuse tracker.
        Map(translated, "REPORT_AUTH_ABUSE_MAX_FAILURES",    "ReportService:AuthAbuseMaxFailures");
        Map(translated, "REPORT_AUTH_ABUSE_WINDOW_SECONDS",  "ReportService:AuthAbuseWindowSeconds");
        Map(translated, "REPORT_AUTH_ABUSE_BAN_SECONDS",     "ReportService:AuthAbuseBanSeconds");

        // Identity + environment label.
        Map(translated, "REPORT_API_KEY",                    "ReportService:ApiKey");
        Map(translated, "REPORT_ENVIRONMENT",                "ReportService:Environment");

        // Admin surface.
        Map(translated, "ADMIN_KEY",                         "Admin:AdminKey");
        Map(translated, "ADMIN_SESSION_MINUTES",             "Admin:SessionMinutes");
        Map(translated, "ADMIN_DEV_AUTO_SIGN_IN",            "Admin:DevAutoSignIn");

        if (translated.Count > 0)
        {
            config.AddInMemoryCollection(translated);
        }
    }

    private static void Map(Dictionary<string, string?> sink, string envVar, string configKey)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(raw)) sink[configKey] = raw.Trim();
    }

    private static void MapScaled(Dictionary<string, string?> sink, string envVar, string configKey, long multiplier)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scaled))
        {
            // Surface bad config loudly rather than silently falling back to the default — an
            // operator who typo'd `REPORT_RETENTION_MAX_MB=10g` wants to know.
            throw new InvalidOperationException(
                $"{envVar}: expected integer, got '{raw.Trim()}'");
        }
        sink[configKey] = checked(scaled * multiplier).ToString(CultureInfo.InvariantCulture);
    }
}
