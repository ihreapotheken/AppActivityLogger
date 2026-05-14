namespace ReportService.Options;

/// <summary>Strongly-typed config for the ingestion service, bound from the <c>ReportService</c> section.</summary>
public sealed record RSCReportServiceOptions
{
    public const string SectionName = "ReportService";

    /// <summary>Shared secret expected in the <c>apiKey</c> request header. Empty fails all auth.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Free-form environment label (e.g. <c>"production"</c>, <c>"staging"</c>) surfaced on the
    /// health endpoint and admin UI so operators can tell which stack a request is hitting. Has no
    /// behavioural effect — purely an identifier. Default keeps existing single-stack deployments
    /// reading "production" without changing config.
    /// </summary>
    public string Environment { get; init; } = "production";

    /// <summary>Filesystem root for stored reports (absolute or CWD-relative).</summary>
    public string ReportsRoot { get; init; } = "reports";

    /// <summary>Hard cap on the whole multipart body (JSON + attachment + framing). Default 500 MiB.</summary>
    public long MaxUploadBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>Hard cap on the optional gzip attachment alone. Default 50 MiB.</summary>
    public long MaxAttachmentBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>
    /// Hard cap on the <c>json</c> part alone, in bytes. Checked before the payload is buffered
    /// into a string. Separate from <see cref="MaxUploadBytes"/> because the envelope cap can be
    /// raised to accommodate a large attachment without implicitly letting the JSON grow too.
    /// </summary>
    public long MaxJsonBytes { get; init; } = 1L * 1024 * 1024;

    /// <summary>
    /// Platform allow-list for both the <c>platform</c> JSON field and <c>{platform}</c> route segments.
    /// Values are normalised to lowercase + deduped on assignment, so configs like
    /// <c>["Android","iOS"]</c> still match lowercase inbound values.
    /// </summary>
    public string[] AllowedPlatforms
    {
        get => _allowedPlatforms;
        init => _allowedPlatforms = Normalize(value);
    }
    private readonly string[] _allowedPlatforms = { "android", "ios" };

    private static string[] Normalize(string[]? raw) =>
        raw is null || raw.Length == 0 ? Array.Empty<string>() :
        raw.Where(p => !string.IsNullOrWhiteSpace(p))
           .Select(p => p.Trim().ToLowerInvariant())
           .Distinct(StringComparer.Ordinal)
           .ToArray();

    /// <summary>Fixed-window per-source-IP rate limit.</summary>
    public int RateLimitPermitsPerMinute { get; init; } = 120;

    /// <summary>Global concurrency cap on the write path. Caps total in-flight uploads across all clients.</summary>
    public int IngestConcurrency { get; init; } = 16;

    /// <summary>Queue slots past <see cref="IngestConcurrency"/>. Over-cap requests fast-reject with <c>429 + Retry-After: 2</c>.</summary>
    public int IngestQueueLimit { get; init; } = 16;

    /// <summary>Per-request wall-clock timeout on the ingest path (seconds).</summary>
    public int IngestTimeoutSeconds { get; init; } = 60;

    /// <summary>Per-statement SQLite command timeout (seconds).</summary>
    public int SqliteCommandTimeoutSeconds { get; init; } = 10;

    /// <summary>Failure threshold (per source) inside <see cref="AuthAbuseWindowSeconds"/> before a ban kicks in.</summary>
    public int AuthAbuseMaxFailures { get; init; } = 10;

    /// <summary>Sliding window in seconds for <see cref="AuthAbuseMaxFailures"/>.</summary>
    public int AuthAbuseWindowSeconds { get; init; } = 60;

    /// <summary>Ban duration in seconds once the threshold is crossed.</summary>
    public int AuthAbuseBanSeconds { get; init; } = 300;

    /// <summary>SQLite file persisting auth-abuse counters (survives process restarts).</summary>
    public string AuthAbuseDbPath { get; init; } = "auth-abuse.db";

    /// <summary>Storage backend: <c>"FileSystem"</c> or <c>"SqliteIndex"</c> (file-system + metadata index).</summary>
    public string Storage { get; init; } = "FileSystem";

    /// <summary>SQLite DB path when <see cref="Storage"/> is <c>"SqliteIndex"</c>. Admin-supplied; not traversal-validated.</summary>
    public string SqliteDbPath { get; init; } = "reports.db";

    /// <summary>SQLite file for the admin audit log. Anchored under <see cref="ReportsRoot"/> when relative.</summary>
    public string AuditDbPath { get; init; } = "audit.db";

    /// <summary>Directory for admin-triggered backups + exports. Anchored under <see cref="ReportsRoot"/> when relative.</summary>
    public string BackupRoot { get; init; } = "backups";

    /// <summary>
    /// Master switch for the background retention sweep. When false, both the size and age caps
    /// below are inert — useful for tests or for deployments that pin storage externally.
    /// </summary>
    public bool RetentionEnabled { get; init; } = true;

    /// <summary>
    /// Hard cap, in bytes, on total persisted reports (JSON + attachment combined, summed across
    /// every platform). When exceeded, the retention sweep deletes oldest-first until the store is
    /// back to ~95% of the cap. <c>0</c> disables the size policy. Default <c>10 GiB</c>.
    /// </summary>
    public long RetentionMaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum age, in days, of a stored report. Any file older than this is deleted on the next
    /// sweep regardless of remaining headroom. <c>0</c> disables the age policy. Default <c>30</c>.
    /// </summary>
    public int RetentionMaxAgeDays { get; init; } = 30;

    /// <summary>How often the background sweep runs, in seconds. Floored at 60s. Default 1 hour.</summary>
    public int RetentionScanIntervalSeconds { get; init; } = 3600;
}
