namespace ReportService.Models;

/// <summary>
/// Closed set of reasons an event lands in <c>analytics_dead_letters</c>. Surfaced on the admin
/// <c>/Analytics/Health</c> page so SDK developers can spot schema drift without trawling logs.
/// </summary>
public static class RSCAnalyticsDeadLetterReasons
{
    public const string SchemaVersionUnsupported = "schema_version_unsupported";
    public const string BatchTooLarge = "batch_too_large";
    public const string EventTooLarge = "event_too_large";
    public const string PropertyTooLarge = "property_too_large";
    public const string PropertyCountExceeded = "property_count_exceeded";
    public const string MissingRequiredField = "missing_required_field";
    public const string InvalidTimestamp = "invalid_timestamp";
    public const string ClockSkew = "clock_skew";
    public const string TypeUnknown = "type_unknown";
    public const string PlatformUnknown = "platform_unknown";
    public const string PiiKeyForbidden = "pii_key_forbidden";
    public const string DuplicateEventId = "duplicate_event_id";
    public const string EmptyBatch = "empty_batch";
}
