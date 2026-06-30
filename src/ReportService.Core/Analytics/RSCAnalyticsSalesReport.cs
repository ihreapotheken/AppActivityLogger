namespace ReportService.Analytics;

/// <summary>
/// Sales / ecommerce projection for the <c>/AnalyticsSales</c> page. Computed at query time from the
/// <c>purchase</c> events the SDKs emit (<c>type = 'ecommerce'</c>, <c>name = 'purchase'</c>) plus a
/// prescription-activity count, scoped to a <c>[from, until)</c> UTC-day window.
/// </summary>
/// <remarks>
/// Unlike the engagement dashboard — which reads the aggregation worker's materialised
/// <c>analytics_daily_rollups</c> — there is no sales rollup table. Purchase events are a small
/// fraction of total traffic (a handful per converting session), so a windowed scan filtered to
/// <c>name = 'purchase'</c> is comparable in cost to <see cref="RSCIAnalyticsStore.GetTopScreensAsync"/>'s
/// <c>type = 'screen'</c> scan, and avoids the migration + aggregation-tick surface a rollup table
/// would add. The monetary fields use <see cref="decimal"/> end to end so cent totals stay exact.
/// </remarks>
public sealed record RSCAnalyticsSalesReport(
    decimal TotalRevenue,
    int OrderCount,
    long ItemsSold,
    string Currency,
    IReadOnlyList<RSCSalesDayPoint> ByDay,
    IReadOnlyList<RSCSalesDimension> ByShippingMethod,
    IReadOnlyList<RSCSalesDimension> ByPaymentMethod,
    IReadOnlyList<RSCSalesItemRow> TopItems,
    IReadOnlyList<RSCSalesActivity> Prescriptions)
{
    /// <summary>The honest-zero report: no purchases (or no events at all) in the window.</summary>
    public static readonly RSCAnalyticsSalesReport Empty = new(
        TotalRevenue: 0m,
        OrderCount: 0,
        ItemsSold: 0,
        Currency: "EUR",
        ByDay: Array.Empty<RSCSalesDayPoint>(),
        ByShippingMethod: Array.Empty<RSCSalesDimension>(),
        ByPaymentMethod: Array.Empty<RSCSalesDimension>(),
        TopItems: Array.Empty<RSCSalesItemRow>(),
        Prescriptions: Array.Empty<RSCSalesActivity>());
}

/// <summary>One UTC day of the revenue trend: revenue booked and orders placed that day.</summary>
public sealed record RSCSalesDayPoint(DateOnly Day, decimal Revenue, int Orders);

/// <summary>Revenue + order count grouped by a categorical purchase dimension (shipping method,
/// payment method, …). <see cref="Key"/> is the raw property value, lower-cased; <c>(unset)</c> when
/// the SDK omitted it.</summary>
public sealed record RSCSalesDimension(string Key, decimal Revenue, int Orders);

/// <summary>One product's contribution across all purchases in the window — units sold and the
/// revenue those units represent (sum of <c>price × quantity</c> over the line items).</summary>
public sealed record RSCSalesItemRow(string ItemId, string? Name, string? Category, long Units, decimal Revenue);

/// <summary>A prescription-funnel activity count (e.g. uploads, scans, cart adds, transfers). Not
/// revenue-bearing — prescription line items don't carry a price in the analytics contract — so it's
/// reported as a plain count alongside the OTC sales numbers.</summary>
public sealed record RSCSalesActivity(string Key, long Count);
