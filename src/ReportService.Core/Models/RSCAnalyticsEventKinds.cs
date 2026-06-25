namespace ReportService.Models;

/// <summary>Canonical analytics event types. The validator rejects events whose
/// <see cref="RSCAnalyticsEvent.Type"/> is outside this set (DLQ reason <c>type_unknown</c>).
/// New types are intentionally a code change rather than a config change — they imply schema
/// expectations the aggregation worker has to honour.</summary>
public static class RSCAnalyticsEventKinds
{
    public const string Screen = "screen";
    public const string Action = "action";
    public const string Ecommerce = "ecommerce";
    public const string Engagement = "engagement";
    public const string Lifecycle = "lifecycle";
    public const string Derived = "derived";
    public const string Error = "error";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Screen, Action, Ecommerce, Engagement, Lifecycle, Derived, Error
    };
}
