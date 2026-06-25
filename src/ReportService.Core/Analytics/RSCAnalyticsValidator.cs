using System.Globalization;
using System.Text.Json;
using ReportService.Models;
using ReportService.Options;

namespace ReportService.Analytics;

/// <summary>
/// Validates a deserialized <see cref="RSCAnalyticsBatch"/> against the analytics options. Splits
/// the batch into accepted + rejected events; a batch-level problem (unsupported schema, oversize)
/// rejects every event with the same reason rather than letting half a payload trickle through.
/// </summary>
public sealed class RSCAnalyticsValidator
{
    private static readonly JsonSerializerOptions ItemsSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RSCAnalyticsOptions _options;
    private readonly HashSet<string> _forbiddenKeys;
    private readonly HashSet<string> _allowedPlatforms;

    public RSCAnalyticsValidator(RSCAnalyticsOptions options, RSCReportServiceOptions reportOptions)
    {
        _options = options;
        _forbiddenKeys = new HashSet<string>(
            (options.ForbiddenPropertyKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        _allowedPlatforms = new HashSet<string>(reportOptions.AllowedPlatforms, StringComparer.Ordinal);
    }

    public RSCAnalyticsValidationResult Validate(RSCAnalyticsBatch batch, DateTimeOffset receivedAt)
    {
        // Batch-level rejections cause every event to land in the DLQ with the same reason. This is
        // deliberate: a schema mismatch or oversize payload is not an event-level bug, and surfacing
        // it once per event would drown the Health page.
        if (batch.SchemaVersion < _options.MinAcceptedSchemaVersion ||
            batch.SchemaVersion > _options.MaxAcceptedSchemaVersion)
        {
            return RejectAll(batch, RSCAnalyticsDeadLetterReasons.SchemaVersionUnsupported,
                $"server accepts [{_options.MinAcceptedSchemaVersion}..{_options.MaxAcceptedSchemaVersion}], got {batch.SchemaVersion}");
        }

        var events = batch.Events ?? Array.Empty<RSCAnalyticsEvent>();
        if (events.Count == 0)
        {
            return new RSCAnalyticsValidationResult(
                BatchRejected: true,
                BatchRejectReason: RSCAnalyticsDeadLetterReasons.EmptyBatch,
                Accepted: Array.Empty<RSCAcceptedAnalyticsEvent>(),
                Rejected: Array.Empty<RSCRejectedAnalyticsEvent>());
        }

        if (events.Count > _options.MaxEventsPerBatch)
        {
            return RejectAll(batch, RSCAnalyticsDeadLetterReasons.BatchTooLarge,
                $"max {_options.MaxEventsPerBatch} events per batch, got {events.Count}");
        }

        var platformLower = (batch.Platform ?? string.Empty).Trim().ToLowerInvariant();
        if (!_allowedPlatforms.Contains(platformLower))
        {
            return RejectAll(batch, RSCAnalyticsDeadLetterReasons.PlatformUnknown,
                $"platform '{batch.Platform}' is not in the allow-list");
        }

        var accepted = new List<RSCAcceptedAnalyticsEvent>(events.Count);
        var rejected = new List<RSCRejectedAnalyticsEvent>();
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            var verdict = ValidateEvent(ev, receivedAt, seenEventIds);
            if (verdict.Accepted is { } ok)
            {
                accepted.Add(ok);
            }
            else if (verdict.Rejected is { } bad)
            {
                rejected.Add(bad);
            }
        }

        return new RSCAnalyticsValidationResult(
            BatchRejected: false,
            BatchRejectReason: null,
            Accepted: accepted,
            Rejected: rejected);
    }

    private RSCAnalyticsValidationResult RejectAll(RSCAnalyticsBatch batch, string reason, string detail)
    {
        var events = batch.Events ?? Array.Empty<RSCAnalyticsEvent>();
        var rejected = new List<RSCRejectedAnalyticsEvent>(events.Count == 0 ? 1 : events.Count);

        if (events.Count == 0)
        {
            // A wholly-empty batch still produces one DLQ row so the Health page can surface that
            // the SDK is sending us nothing useful.
            rejected.Add(new RSCRejectedAnalyticsEvent(
                EventId: null,
                Reason: reason,
                Detail: detail,
                RawJson: JsonSerializer.Serialize(batch)));
        }
        else
        {
            foreach (var ev in events)
            {
                rejected.Add(new RSCRejectedAnalyticsEvent(
                    EventId: ev.EventId,
                    Reason: reason,
                    Detail: detail,
                    RawJson: JsonSerializer.Serialize(ev)));
            }
        }

        return new RSCAnalyticsValidationResult(
            BatchRejected: true,
            BatchRejectReason: reason,
            Accepted: Array.Empty<RSCAcceptedAnalyticsEvent>(),
            Rejected: rejected);
    }

    private (RSCAcceptedAnalyticsEvent? Accepted, RSCRejectedAnalyticsEvent? Rejected)
        ValidateEvent(RSCAnalyticsEvent ev, DateTimeOffset receivedAt, HashSet<string> seenEventIds)
    {
        RSCRejectedAnalyticsEvent Reject(string reason, string? detail = null)
            => new(ev.EventId, reason, detail, JsonSerializer.Serialize(ev));

        if (string.IsNullOrWhiteSpace(ev.EventId) ||
            string.IsNullOrWhiteSpace(ev.SessionId) ||
            string.IsNullOrWhiteSpace(ev.Type) ||
            string.IsNullOrWhiteSpace(ev.Name) ||
            string.IsNullOrWhiteSpace(ev.OccurredAt))
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.MissingRequiredField,
                "eventId/sessionId/type/name/occurredAt are required"));
        }

        if (!seenEventIds.Add(ev.EventId))
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.DuplicateEventId,
                $"eventId '{ev.EventId}' appeared more than once in the same batch"));
        }

        if (!RSCAnalyticsEventKinds.Known.Contains(ev.Type))
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.TypeUnknown,
                $"unknown event type '{ev.Type}'"));
        }

        if (!DateTimeOffset.TryParseExact(ev.OccurredAt, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var occurredAt) &&
            !DateTimeOffset.TryParse(ev.OccurredAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out occurredAt))
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.InvalidTimestamp,
                $"occurredAt '{ev.OccurredAt}' is not parseable as ISO-8601"));
        }

        var skewSeconds = Math.Abs((receivedAt - occurredAt).TotalSeconds);
        if (skewSeconds > _options.MaxClockSkewSeconds)
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.ClockSkew,
                $"occurredAt differs from server time by {skewSeconds:F0}s (max {_options.MaxClockSkewSeconds}s)"));
        }

        var rawProps = ev.Properties ?? new Dictionary<string, string>(0);
        if (rawProps.Count > _options.MaxPropertiesPerEvent)
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyCountExceeded,
                $"event has {rawProps.Count} properties; max is {_options.MaxPropertiesPerEvent}"));
        }

        var normalized = new Dictionary<string, string>(rawProps.Count, StringComparer.Ordinal);
        foreach (var (key, value) in rawProps)
        {
            if (string.IsNullOrEmpty(key) || key.Length > _options.MaxPropertyKeyLength)
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyTooLarge,
                    $"property key length must be 1..{_options.MaxPropertyKeyLength}"));
            }
            if (value is { } v && v.Length > _options.MaxPropertyValueLength)
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyTooLarge,
                    $"property '{key}' value length {v.Length} exceeds max {_options.MaxPropertyValueLength}"));
            }
            if (_forbiddenKeys.Contains(key.ToLowerInvariant()))
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PiiKeyForbidden,
                    $"property '{key}' is forbidden — analytics must not carry PII"));
            }

            normalized[key] = value ?? string.Empty;
        }

        var items = ev.Items ?? Array.Empty<RSCAnalyticsItem>();

        return (new RSCAcceptedAnalyticsEvent(
            EventId: ev.EventId,
            SessionId: ev.SessionId,
            Sequence: ev.Sequence,
            OccurredAt: occurredAt,
            Type: ev.Type,
            Name: ev.Name,
            Screen: ev.Screen,
            Feature: ev.Feature,
            DurationMs: ev.DurationMs,
            Properties: normalized,
            Items: items), null);
    }

    /// <summary>JSON used to persist the items payload on accepted rows.</summary>
    public static string SerializeItems(IReadOnlyList<RSCAnalyticsItem> items) =>
        items.Count == 0 ? "[]" : JsonSerializer.Serialize(items, ItemsSerializerOptions);

    /// <summary>JSON used to persist the property bag on accepted rows.</summary>
    public static string SerializeProperties(IReadOnlyDictionary<string, string> properties) =>
        properties.Count == 0 ? "{}" : JsonSerializer.Serialize(properties);
}
