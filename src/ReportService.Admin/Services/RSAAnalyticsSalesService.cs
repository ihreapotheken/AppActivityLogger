using ReportService.Admin.ViewModels;
using ReportService.Analytics;

namespace ReportService.Admin.Services;

/// <summary>Builds the <c>/AnalyticsSales</c> dashboard from the analytics store's sales projection.</summary>
public interface IRSAAnalyticsSalesService
{
    ValueTask<RSAAnalyticsSalesVM> BuildAsync(RSCAnalyticsScope scope, CancellationToken ct);
}

/// <summary>
/// Reads <see cref="RSCIAnalyticsStore.GetSalesReportAsync"/> for the trailing 30-day window and
/// shapes it into the page view-model: zero-filled daily trend, revenue/order breakdowns by shipping
/// and payment method, top products, and prescription activity. Stays thin — all aggregation lives in
/// the store; this layer only zero-fills the trend and maps raw enum-ish keys to display labels.
/// </summary>
public sealed class RSAAnalyticsSalesService : IRSAAnalyticsSalesService
{
    private const int TrendDays = 30;
    private const int TopItems = 10;

    private readonly RSCIAnalyticsStore _store;

    public RSAAnalyticsSalesService(RSCIAnalyticsStore store) => _store = store;

    public async ValueTask<RSAAnalyticsSalesVM> BuildAsync(RSCAnalyticsScope scope, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var firstDay = today.AddDays(-(TrendDays - 1));
        var from = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);

        var report = await _store.GetSalesReportAsync(from, until, scope, TopItems, ct).ConfigureAwait(false);

        // Zero-fill every day in the window so the trend lines stay continuous on no-sale days.
        var byDay = report.ByDay.ToDictionary(p => p.Day, p => (p.Revenue, p.Orders));
        var trend = new RSASalesTrendPointVM[TrendDays];
        for (var i = 0; i < TrendDays; i++)
        {
            var day = firstDay.AddDays(i);
            byDay.TryGetValue(day, out var v);
            trend[i] = new RSASalesTrendPointVM(day.ToString("MM-dd"), v.Revenue, v.Orders);
        }

        var aov = report.OrderCount == 0 ? 0m : report.TotalRevenue / report.OrderCount;

        var shipping = report.ByShippingMethod
            .Select(d => new RSASalesBreakdownVM(PrettyShipping(d.Key), d.Revenue, d.Orders))
            .ToArray();
        var payment = report.ByPaymentMethod
            .Select(d => new RSASalesBreakdownVM(PrettyPayment(d.Key), d.Revenue, d.Orders))
            .ToArray();
        var items = report.TopItems
            .Select(i => new RSASalesItemVM(i.ItemId, string.IsNullOrWhiteSpace(i.Name) ? i.ItemId : i.Name!, i.Category, i.Units, i.Revenue))
            .ToArray();
        var prescriptions = report.Prescriptions
            .Select(a => new RSASalesActivityVM(PrettyPrescription(a.Key), a.Count))
            .ToArray();

        return new RSAAnalyticsSalesVM(
            TotalRevenue: report.TotalRevenue,
            Orders: report.OrderCount,
            AverageOrderValue: aov,
            ItemsSold: report.ItemsSold,
            Currency: report.Currency,
            RevenueTrend: trend,
            ShippingMethods: shipping,
            PaymentMethods: payment,
            TopItems: items,
            Prescriptions: prescriptions);
    }

    // The SDK reports these as lower-cased identifiers (shipping_method / payment_method on the
    // purchase event); map the known ones to friendly labels and Title-case anything new so an
    // unrecognised value still renders sensibly instead of as a raw token.
    private static string PrettyShipping(string key) => key switch
    {
        "standard" => "Standard",
        "express" => "Express",
        "pickup" => "Pharmacy pickup",
        "home_delivery" => "Home delivery",
        "delivery_by_agreement" => "By agreement",
        "(unset)" => "(unset)",
        _ => TitleCase(key),
    };

    private static string PrettyPayment(string key) => key switch
    {
        "paypal" => "PayPal",
        "credit_card" => "Credit card",
        "sepa_debit" => "SEPA direct debit",
        "invoice" => "Invoice",
        "apple_pay" => "Apple Pay",
        "google_pay" => "Google Pay",
        "(unset)" => "(unset)",
        _ => TitleCase(key),
    };

    private static string PrettyPrescription(string key) => key switch
    {
        "prescription_upload_success" => "Uploaded",
        "prescription_scan_result" => "Scanned",
        "prescription_cart_add" => "Added to cart",
        "prescription_transfer" => "Sent to pharmacy",
        _ => TitleCase(key),
    };

    private static string TitleCase(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var words = raw.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
