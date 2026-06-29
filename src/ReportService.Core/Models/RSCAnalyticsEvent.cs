namespace ReportService.Models;

/// <summary>
/// One analytics event inside an <see cref="RSCAnalyticsBatch"/>. The server enforces a small
/// required set (<see cref="EventId"/>, <see cref="SessionId"/>, <see cref="Type"/>,
/// <see cref="Name"/>, <see cref="OccurredAt"/>) and preserves the rest as a flat key/value
/// dictionary in <see cref="Properties"/>.
/// </summary>
/// <remarks>
/// Idempotency: the server enforces <c>UNIQUE(platform, event_id)</c> at the storage layer, so a
/// retried batch which already landed produces zero duplicate rows. <see cref="Sequence"/> is a
/// per-session monotonically increasing counter that lets the aggregation worker reconstruct
/// session timelines even when batches arrive out of order.
/// </remarks>
public sealed record RSCAnalyticsEvent(
    string EventId,
    string SessionId,
    long Sequence,
    string OccurredAt,
    string Type,
    string Name,
    string? Screen,
    string? Feature,
    long? DurationMs,
    IReadOnlyDictionary<string, string>? Properties,
    IReadOnlyList<RSCAnalyticsItem>? Items
);

/// <summary>
/// Optional ecommerce/list payload attached to an event (e.g. cart, product list). The shape is
/// intentionally narrow — anything richer goes in the parent event's <c>Properties</c> bag.
/// </summary>
/// <remarks>
/// <c>ItemId</c> is required. Although the type is non-nullable, System.Text.Json does not enforce
/// non-nullable reference types, so an <c>items[]</c> entry that omits <c>itemId</c> deserializes to
/// null. The validator rejects any event carrying an item with a null/blank <c>ItemId</c>
/// (<c>missing_required_field</c>) so the non-null contract holds for stored rows.
/// </remarks>
public sealed record RSCAnalyticsItem(
    string ItemId,
    string? Name,
    string? Category,
    decimal? Price,
    int? Quantity,
    string? Currency
);
