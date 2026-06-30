using System.Globalization;
using System.Text.Json;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage.Catalog;

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
    private readonly RSCICatalog _catalog;
    private readonly RSCCatalogOptions _catalogOptions;
    private readonly HashSet<string> _forbiddenKeys;
    // Two distinct allow-lists keep the SDK and server endpoints from leaking each other's
    // platforms. The SDK path (POST /api/v2/analytics/events) only accepts the problem-report
    // platforms (android/ios); the server path (POST /api/v2/analytics/server-events) additionally
    // accepts the analytics-only ServerPlatforms (default "backend"). A single shared union would
    // let an SDK client (anything holding the SDK apiKey) post platform=backend and inject events
    // into the trusted first-party bucket operators read as server-verified.
    private readonly HashSet<string> _sdkPlatforms;
    private readonly HashSet<string> _serverPlatforms;

    // Defensively-clamped copies of the numeric caps. RSCAnalyticsOptions.Validate() surfaces a
    // mis-set value at startup when the host wires ValidateOnStart, but a non-positive (or inverted)
    // value that slips through must not silently reject EVERY event here — clamp to a usable floor
    // so the pipeline degrades to "accepts using a sane minimum" rather than "dead-letters all".
    private readonly int _maxEventsPerBatch;
    private readonly int _maxPropertiesPerEvent;
    private readonly int _maxPropertyValueLength;
    private readonly int _maxPropertyKeyLength;
    private readonly int _maxClockSkewSeconds;

    public RSCAnalyticsValidator(
        RSCAnalyticsOptions options,
        RSCReportServiceOptions reportOptions,
        RSCICatalog catalog,
        RSCCatalogOptions catalogOptions)
    {
        _options = options;
        _catalog = catalog;
        _catalogOptions = catalogOptions;
        _maxEventsPerBatch = Math.Max(1, options.MaxEventsPerBatch);
        _maxPropertiesPerEvent = Math.Max(1, options.MaxPropertiesPerEvent);
        _maxPropertyValueLength = Math.Max(1, options.MaxPropertyValueLength);
        _maxPropertyKeyLength = Math.Max(1, options.MaxPropertyKeyLength);
        _maxClockSkewSeconds = Math.Max(1, options.MaxClockSkewSeconds);
        _forbiddenKeys = new HashSet<string>(
            (options.ForbiddenPropertyKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        // Base set: the problem-report platforms (android/ios). Lowercased to match the
        // case-folded lookup in Validate(). This is the only set the SDK path is allowed to use.
        _sdkPlatforms = new HashSet<string>(
            (reportOptions.AllowedPlatforms ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        // Server set: base + the analytics-only ServerPlatforms. A backend may still attribute
        // events to android/ios when it knows the user's device, so this is a superset.
        _serverPlatforms = new HashSet<string>(_sdkPlatforms, StringComparer.Ordinal);
        foreach (var p in (options.ServerPlatforms ?? Array.Empty<string>())
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Select(p => p.Trim().ToLowerInvariant()))
        {
            _serverPlatforms.Add(p);
        }
    }

    /// <param name="batch">The deserialized batch envelope to validate.</param>
    /// <param name="receivedAt">Server receive time, used for the per-event clock-skew check.</param>
    /// <param name="allowServerPlatforms">
    /// When <c>false</c> (the default, used by the SDK ingestion path) only the problem-report
    /// platforms (android/ios) are accepted. When <c>true</c> (the server-events path) the
    /// analytics-only <see cref="RSCAnalyticsOptions.ServerPlatforms"/> (e.g. <c>backend</c>) are
    /// additionally accepted. Scoping the allow-list per origin keeps the SDK endpoint from being
    /// able to inject events into the trusted server-only platform bucket.
    /// </param>
    public RSCAnalyticsValidationResult Validate(
        RSCAnalyticsBatch batch, DateTimeOffset receivedAt, bool allowServerPlatforms = false)
    {
        var allowedPlatforms = allowServerPlatforms ? _serverPlatforms : _sdkPlatforms;
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

        if (events.Count > _maxEventsPerBatch)
        {
            return RejectAll(batch, RSCAnalyticsDeadLetterReasons.BatchTooLarge,
                $"max {_maxEventsPerBatch} events per batch, got {events.Count}");
        }

        var platformLower = (batch.Platform ?? string.Empty).Trim().ToLowerInvariant();
        if (!allowedPlatforms.Contains(platformLower))
        {
            return RejectAll(batch, RSCAnalyticsDeadLetterReasons.PlatformUnknown,
                $"platform '{batch.Platform}' is not in the allow-list");
        }

        // Tenancy validation (batch-level, like platform). The client is the top-level tenant: it's
        // derived from the authenticated API key by the ingestion layer (not the body) and apps are
        // nested under it, so we validate client first, then the app within that client, then the
        // environment the client declared for that app. We coalesce null → the configured default so
        // direct callers (seeder/tests) and key-less/root traffic resolve to the seeded default
        // tenant. Unknown ⇒ whole batch rejected, mirroring platform_unknown.
        if (_catalogOptions.Enabled)
        {
            var clientSlug = Coalesce(batch.ClientId, _catalogOptions.DefaultClientSlug);
            var appSlug = Coalesce(batch.AppId, _catalogOptions.DefaultAppSlug);
            var environment = Coalesce(batch.Environment, _catalogOptions.DefaultEnvironment);

            if (!_catalog.IsValidClient(clientSlug))
                return RejectAll(batch, RSCAnalyticsDeadLetterReasons.ClientUnknown,
                    $"client '{clientSlug}' is not registered");
            if (!_catalog.IsValidApp(clientSlug, appSlug))
                return RejectAll(batch, RSCAnalyticsDeadLetterReasons.AppUnknown,
                    $"app '{appSlug}' is not registered for client '{clientSlug}'");
            if (!_catalog.IsValidEnvironment(clientSlug, appSlug, environment))
                return RejectAll(batch, RSCAnalyticsDeadLetterReasons.EnvironmentUnknown,
                    $"environment '{environment}' is not declared for app '{appSlug}' (client '{clientSlug}')");
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

    // Trim+lowercase the supplied tenancy value, falling back to the configured default when blank.
    // Mirrors RSCCatalogSlug.Normalize so registry lookups match what the store stamps.
    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback.Trim().ToLowerInvariant() : value.Trim().ToLowerInvariant();

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

        // eventId / sessionId are stored VERBATIM (never hashed) into analytics_events and are
        // surfaced on the admin pages + NDJSON exports. They must be opaque, non-PII keys. Cap
        // their length and reject control characters so a caller cannot route an over-long or
        // line-break-bearing value (which would corrupt the NDJSON export) into those columns.
        if (!IsValidEnvelopeIdentifier(ev.EventId, out var eventIdReason))
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.MissingRequiredField,
                $"eventId is invalid: {eventIdReason}"));
        }
        if (!IsValidEnvelopeIdentifier(ev.SessionId, out var sessionIdReason))
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.MissingRequiredField,
                $"sessionId is invalid: {sessionIdReason}"));
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
        if (skewSeconds > _maxClockSkewSeconds)
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.ClockSkew,
                $"occurredAt differs from server time by {skewSeconds:F0}s (max {_maxClockSkewSeconds}s)"));
        }

        var rawProps = ev.Properties ?? new Dictionary<string, string>(0);
        if (rawProps.Count > _maxPropertiesPerEvent)
        {
            return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyCountExceeded,
                $"event has {rawProps.Count} properties; max is {_maxPropertiesPerEvent}"));
        }

        var normalized = new Dictionary<string, string>(rawProps.Count, StringComparer.Ordinal);
        foreach (var (key, value) in rawProps)
        {
            if (string.IsNullOrEmpty(key) || key.Length > _maxPropertyKeyLength)
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyTooLarge,
                    $"property key length must be 1..{_maxPropertyKeyLength}"));
            }
            if (value is { } v && v.Length > _maxPropertyValueLength)
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyTooLarge,
                    $"property '{key}' value length {v.Length} exceeds max {_maxPropertyValueLength}"));
            }
            if (_forbiddenKeys.Contains(key.ToLowerInvariant()))
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PiiKeyForbidden,
                    $"property '{key}' is forbidden — analytics must not carry PII"));
            }

            normalized[key] = value ?? string.Empty;
        }

        var items = ev.Items ?? Array.Empty<RSCAnalyticsItem>();
        // RSCAnalyticsItem.ItemId is a non-nullable record positional, but System.Text.Json does
        // not enforce non-nullable reference types, so an items[] entry that omits "itemId"
        // deserializes to ItemId == null. Reject rather than persist a contract-violating row that
        // downstream consumers (exports, dashboards, funnels) could NRE on.
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemId))
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.MissingRequiredField,
                    "every item requires a non-blank itemId"));
            }
            if (item.ItemId.Length > _maxPropertyValueLength)
            {
                return (null, Reject(RSCAnalyticsDeadLetterReasons.PropertyTooLarge,
                    $"item itemId length {item.ItemId.Length} exceeds max {_maxPropertyValueLength}"));
            }
        }

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

    /// <summary>
    /// Upper bound on a verbatim-stored envelope identifier (eventId/sessionId), in characters.
    /// Generous enough for GUIDs and business keys like <c>purchase-&lt;orderId&gt;</c>, but bounds the
    /// size of a value a naive caller might route into the un-hashed, exported id columns.
    /// </summary>
    public const int MaxIdentifierLength = 256;

    /// <summary>
    /// Validates that a caller-supplied envelope identifier (eventId/sessionId) is safe to store
    /// verbatim: not over-long, and free of control characters (newlines/tabs would corrupt the
    /// newline-delimited NDJSON export). It deliberately does NOT enforce a tight charset so opaque
    /// keys remain free-form; the contract requires these to be non-PII keys.
    /// </summary>
    private static bool IsValidEnvelopeIdentifier(string value, out string reason)
    {
        if (value.Length > MaxIdentifierLength)
        {
            reason = $"length {value.Length} exceeds max {MaxIdentifierLength}";
            return false;
        }
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                reason = "contains control characters";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>JSON used to persist the items payload on accepted rows.</summary>
    public static string SerializeItems(IReadOnlyList<RSCAnalyticsItem> items) =>
        items.Count == 0 ? "[]" : JsonSerializer.Serialize(items, ItemsSerializerOptions);

    /// <summary>JSON used to persist the property bag on accepted rows.</summary>
    public static string SerializeProperties(IReadOnlyDictionary<string, string> properties) =>
        properties.Count == 0 ? "{}" : JsonSerializer.Serialize(properties);
}
