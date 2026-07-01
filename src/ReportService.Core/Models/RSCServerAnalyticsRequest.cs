namespace ReportService.Models;

/// <summary>
/// Well-known analytics platforms that originate server-side rather than from a mobile SDK.
/// Kept separate from the <c>RSCReportServiceOptions</c> problem-report platform allow-list so
/// adding a server source never creates a <c>reports/&lt;platform&gt;/</c> folder or widens what the
/// problem-report endpoints accept.
/// </summary>
public static class RSCAnalyticsPlatforms
{
    /// <summary>Default attribution for first-party events reported by a trusted backend service.</summary>
    public const string Backend = "backend";
}

/// <summary>
/// Backend-facing request for <c>POST /api/v2/analytics/server-events</c>. Lets a trusted
/// first-party service report analytics events (e.g. a completed purchase) directly, instead of
/// relying on the mobile SDK to emit them. The shape is deliberately lighter than
/// <see cref="RSCAnalyticsBatch"/>: the server fills in the SDK-centric envelope fields
/// (sessionId, sequence, batchId, sdkVersion, generatedAt) so the caller only supplies what it
/// actually knows, then the events flow through the exact same validation, hashing, storage,
/// aggregation, and funnel pipeline as SDK batches.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="Platform"/>: attribution. Defaults to <see cref="RSCAnalyticsPlatforms.Backend"/>;
///         a caller that knows the user's device may pass <c>"android"</c>/<c>"ios"</c> so the event
///         joins that platform's sessions/funnels.</item>
///   <item><see cref="SubjectId"/>: the user/account identifier to attribute the events to. Hashed
///         with the configured pepper before storage — never persisted verbatim (same treatment as
///         the SDK's <c>anonymousId</c>).</item>
///   <item><see cref="BatchId"/>: optional; a server-generated id is used when omitted. Supply a
///         stable id to make a whole-request retry traceable.</item>
/// </list>
/// <para>
/// Contract notes:
/// <list type="bullet">
///   <item>Empty/absent <c>events</c> array is a <c>400 Bad Request</c> (a structurally-invalid
///         request), and an event missing the required <see cref="RSCServerAnalyticsEvent.Name"/> is
///         likewise a <c>400</c>. A batch that reaches the store but is <b>fully rejected</b> (unknown
///         platform/app/client, schema mismatch, or every event dead-lettered so nothing is accepted)
///         is also a <c>400</c> — the receipt is still returned as the response body so the caller can
///         read the reject reason. A <b>partial</b> accept (at least one event stored) or an idempotent
///         all-duplicates replay stays <c>202 Accepted</c>; inspect the receipt for per-event outcomes.
///         Both the SDK <c>/events</c> route and this <c>/server-events</c> route share this single
///         rule (<c>RSAnalyticsIngestionResult.FromReceipt</c>) so the status can't drift between
///         them.</item>
///   <item><b>Never put PII in the envelope ids.</b> <see cref="RSCServerAnalyticsEvent.EventId"/> and
///         <see cref="RSCServerAnalyticsEvent.SessionId"/> are stored <i>verbatim</i> (unlike
///         <see cref="SubjectId"/>/<see cref="ClientId"/>, which are hashed) and are surfaced on the
///         admin pages and NDJSON exports. They must be opaque, non-PII business keys. A request whose
///         eventId or sessionId equals the <see cref="SubjectId"/>/<see cref="ClientId"/> is rejected
///         with <c>400</c>; over-long ids or ids containing control characters are dead-lettered.</item>
/// </list>
/// </para>
/// </remarks>
public sealed record RSCServerAnalyticsRequest(
    string? Platform,
    string? SubjectId,
    string? ClientId,
    string? Source,
    string? BatchId,
    IReadOnlyList<RSCServerAnalyticsEvent>? Events,
    string? AppId = null,
    string? Environment = null
);

/// <summary>
/// One server-reported analytics event. Only <see cref="Name"/> is required; everything else is
/// synthesized when omitted. For idempotency supply a stable <see cref="EventId"/> derived from the
/// business key (e.g. <c>"purchase-&lt;orderId&gt;"</c>) so a retried report is deduped on the
/// existing <c>UNIQUE(platform, event_id)</c> constraint instead of double-counting.
/// </summary>
/// <param name="Name">Required. Omitting it (null/blank for any event) is a <c>400</c>.</param>
/// <param name="EventId">
/// Optional opaque business key; a server-generated id is used when omitted. Stored verbatim and
/// exported, so it MUST be a non-PII key and must not equal the request's subjectId/clientId
/// (rejected with <c>400</c>). Supply a stable value for idempotent retries.
/// </param>
/// <param name="SessionId">
/// Optional. Stored verbatim and exported — must be a non-PII key (same rules as
/// <paramref name="EventId"/>). When omitted, a per-event session id is synthesized from the eventId
/// (<c>srv-{eventId}</c>) so unrelated server events are NOT collapsed into one synthetic session.
/// Supply an explicit sessionId only when the events genuinely share a session.
/// </param>
/// <param name="Sequence">
/// Optional per-session ordering counter. When omitted it is synthesized as the event's position
/// within THIS request, so it is only meaningful within a single request. A caller that reuses a
/// stable sessionId across multiple requests should supply explicit monotonic Sequence values;
/// otherwise each request restarts at 0 and the per-session timeline ordering for those events falls
/// back to occurredAt (rollup counts are unaffected).
/// </param>
/// <param name="OccurredAt">
/// Optional ISO-8601 timestamp; defaults to the server's receive time when omitted (which always
/// passes the clock-skew check). NOTE: a supplied timestamp that differs from server time by more
/// than <see cref="Options.RSCAnalyticsOptions.MaxClockSkewSeconds"/> (default 86400s = 24h, applied
/// symmetrically) is dead-lettered as <c>clock_skew</c> and the receipt reports it as rejected — the
/// request is <c>202</c> if other events in the batch were accepted, or <c>400</c> if this leaves the
/// batch fully rejected. This matters for backfill/replay of historically-dated events: either
/// omit OccurredAt so it defaults to now, or have the operator widen <c>MaxClockSkewSeconds</c> to
/// cover the expected backfill horizon.
/// </param>
/// <param name="Type">Event type; defaults to <c>action</c> when omitted. Use <c>ecommerce</c> for purchases.</param>
/// <param name="Screen">Optional originating screen name.</param>
/// <param name="Feature">Optional feature/area tag (e.g. <c>otc</c>).</param>
/// <param name="DurationMs">Optional duration in milliseconds.</param>
/// <param name="Properties">Optional flat string key/value bag (subject to the analytics property caps + PII-key guard).</param>
/// <param name="Items">Optional ecommerce line items.</param>
public sealed record RSCServerAnalyticsEvent(
    string? Name,
    string? Type,
    string? EventId,
    string? SessionId,
    long? Sequence,
    string? OccurredAt,
    string? Screen,
    string? Feature,
    long? DurationMs,
    IReadOnlyDictionary<string, string>? Properties,
    IReadOnlyList<RSCAnalyticsItem>? Items
);
