namespace ReportService.Admin.ViewModels;

/// <summary>
/// Sales / revenue dashboard for the <c>/AnalyticsSales</c> page. Built from the store's
/// <c>RSCAnalyticsSalesReport</c> (purchase events + prescription activity) and shaped for display:
/// monetary values stay <see cref="decimal"/>, the trend is zero-filled to a full 30-day window, and
/// the categorical breakdowns carry human-readable labels.
/// </summary>
public sealed record RSAAnalyticsSalesVM(
    decimal TotalRevenue,
    int Orders,
    decimal AverageOrderValue,
    long ItemsSold,
    string Currency,
    IReadOnlyList<RSASalesTrendPointVM> RevenueTrend,
    IReadOnlyList<RSASalesBreakdownVM> ShippingMethods,
    IReadOnlyList<RSASalesBreakdownVM> PaymentMethods,
    IReadOnlyList<RSASalesItemVM> TopItems,
    IReadOnlyList<RSASalesActivityVM> Prescriptions);

/// <summary>One day of the sales trend: revenue booked and orders placed. <see cref="Label"/> is the
/// pre-formatted x-axis label ("MM-dd"), oldest → newest.</summary>
public sealed record RSASalesTrendPointVM(string Label, decimal Revenue, int Orders);

/// <summary>Revenue + order count for one categorical purchase dimension (a shipping or payment
/// method), with a display-ready <see cref="Label"/>.</summary>
public sealed record RSASalesBreakdownVM(string Label, decimal Revenue, int Orders);

/// <summary>One row of the top-selling-products table.</summary>
public sealed record RSASalesItemVM(string ItemId, string Name, string? Category, long Units, decimal Revenue);

/// <summary>A prescription-funnel activity count with a display-ready <see cref="Label"/>.</summary>
public sealed record RSASalesActivityVM(string Label, long Count);
