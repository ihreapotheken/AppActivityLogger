namespace ReportService.Models;

/// <summary>
/// On-the-wire batch envelope for the v2 analytics ingestion endpoint
/// (<c>POST /api/v2/analytics/events</c>). One <see cref="RSCAnalyticsBatch"/> carries up to a few
/// hundred individual events from one SDK install, identified by <see cref="AnonymousId"/>.
/// </summary>
/// <remarks>
/// Field names are on the wire — both the Android and iOS SDKs serialize this exact shape.
/// <list type="bullet">
///   <item><see cref="SchemaVersion"/>: integer. Minor (additive) revisions are accepted by older
///         servers — unknown event/property fields are preserved into the event's
///         <see cref="RSCAnalyticsEvent.Properties"/>. A different major rejects the whole batch
///         to the dead-letter queue with reason <c>schema_version_unsupported</c>.</item>
///   <item><see cref="BatchId"/>: SDK-generated GUID. Used for idempotency + traceability;
///         a retry of the same batch is deduped on this ID alone.</item>
///   <item><see cref="AnonymousId"/>: stable per-install identifier. Hashed server-side with the
///         configured pepper before any row reaches the rollup tables. Never stored verbatim.</item>
///   <item><see cref="ClientId"/>: the client/tenant the events belong to (e.g. a pharmacy id).
///         A tenancy differentiator stored <b>verbatim</b> (validated against the catalog), <b>not</b>
///         hashed — it is a business key, not user PII. Omitted ⇒ the configured default client.</item>
///   <item><see cref="AppId"/> / <see cref="Environment"/>: the app slug + environment the batch
///         belongs to. May also be supplied via the <c>X-Analytics-App</c> / <c>X-Analytics-Environment</c>
///         request headers; omitted ⇒ the configured defaults. Validated against the catalog.</item>
/// </list>
/// </remarks>
public sealed record RSCAnalyticsBatch(
    int SchemaVersion,
    string BatchId,
    string Platform,
    string SdkVersion,
    string? HostAppVersion,
    string? AnonymousId,
    string? ClientId,
    string GeneratedAt,
    IReadOnlyList<RSCAnalyticsEvent> Events,
    string? AppId = null,
    string? Environment = null
);
