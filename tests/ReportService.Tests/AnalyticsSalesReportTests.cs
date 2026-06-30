using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Integration tests for <see cref="RSCIAnalyticsStore.GetSalesReportAsync"/> against the real SQLite
/// store: writes <c>purchase</c> events (with items + payment/shipping properties) plus a
/// prescription event, then asserts the revenue totals, per-day trend, payment/shipping breakdowns,
/// top products, and prescription activity that back the <c>/AnalyticsSales</c> page.
/// </summary>
public class AnalyticsSalesReportTests : IDisposable
{
    private readonly string _root;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;

    public AnalyticsSalesReportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rs-sales-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        var analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-sales-test.db",
            IdentifierHashPepper = "pepper-test",
        };
        _store = new RSCSqliteAnalyticsStore(reportOptions, analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        _validator = new RSCAnalyticsValidator(analyticsOptions, reportOptions, RSATestCatalog.Permissive, new ReportService.Options.RSCCatalogOptions());
        _hasher = new RSCAnalyticsIdentifierHasher(analyticsOptions);
    }

    // One event per batch with receivedAt anchored near occurredAt, so the validator's clock-skew
    // guard (24h) never trips even when events sit on different days.
    private async Task WriteAsync(string platform, RSCAnalyticsEvent ev, DateTimeOffset occurredAt)
    {
        var batch = new RSCAnalyticsBatch(
            SchemaVersion: 1, BatchId: Guid.NewGuid().ToString(), Platform: platform,
            SdkVersion: "2.0.0", HostAppVersion: "4.5.0", AnonymousId: "anon-1",
            ClientId: null, GeneratedAt: occurredAt.ToString("O"), Events: new[] { ev });
        var receivedAt = occurredAt.AddMinutes(1);
        var verdict = _validator.Validate(batch, receivedAt);
        var receipt = await _store.WriteBatchAsync(batch, _hasher.Hash("anon-1"), null, verdict, receivedAt, default);
        Assert.Equal(1, receipt.AcceptedCount);
    }

    private static RSCAnalyticsEvent Purchase(
        string id, DateTimeOffset at, string total, string shipping, string payment, params RSCAnalyticsItem[] items) =>
        new(EventId: id, SessionId: "ses-" + id, Sequence: 0, OccurredAt: at.ToString("O"),
            Type: "ecommerce", Name: "purchase", Screen: "cart", Feature: "otc", DurationMs: null,
            Properties: new Dictionary<string, string>
            {
                ["order_id"] = "ord-" + id,
                ["items_count"] = items.Length.ToString(),
                ["total"] = total,
                ["currency"] = "EUR",
                ["shipping_method"] = shipping,
                ["payment_method"] = payment,
            },
            Items: items);

    [Fact]
    public async Task Sales_report_aggregates_revenue_orders_items_and_breakdowns()
    {
        var now = DateTimeOffset.UtcNow;
        var dayA = now.AddDays(-2);
        var dayB = now.AddDays(-1);

        // Order A: 2× aspirin @5.00 + 1× vitamin C @5.00 = 15.00, paypal / standard.
        await WriteAsync("android", Purchase("a1", dayA, "15.00", "standard", "paypal",
            new RSCAnalyticsItem("sku-asp", "Aspirin", "pain", 5.00m, 2, "EUR"),
            new RSCAnalyticsItem("sku-vit", "Vitamin C", "supplements", 5.00m, 1, "EUR")), dayA);

        // Order B: 2× aspirin @4.00 = 8.00, credit_card / express.
        await WriteAsync("android", Purchase("b1", dayB, "8.00", "express", "credit_card",
            new RSCAnalyticsItem("sku-asp", "Aspirin", "pain", 4.00m, 2, "EUR")), dayB);

        // A prescription transfer on day A — counted under prescription activity, not revenue.
        await WriteAsync("android", new RSCAnalyticsEvent(
            EventId: "rx1", SessionId: "ses-rx1", Sequence: 0, OccurredAt: dayA.ToString("O"),
            Type: "action", Name: "prescription_transfer", Screen: null, Feature: "rx", DurationMs: null,
            Properties: new Dictionary<string, string> { ["to_pharmacy_id_hash"] = "ph-x" }, Items: null), dayA);

        var from = now.AddDays(-5);
        var until = now.AddDays(1);
        var report = await _store.GetSalesReportAsync(from, until, RSCAnalyticsScope.All, topItems: 10, default);

        Assert.Equal(2, report.OrderCount);
        Assert.Equal(23.00m, report.TotalRevenue);
        Assert.Equal(5, report.ItemsSold);
        Assert.Equal("EUR", report.Currency);

        // Two distinct sale days, each one order.
        Assert.Equal(2, report.ByDay.Count);
        Assert.All(report.ByDay, d => Assert.Equal(1, d.Orders));
        Assert.Equal(23.00m, report.ByDay.Sum(d => d.Revenue));

        // Payment breakdown — sorted by revenue desc, so paypal (15) precedes credit_card (8).
        Assert.Equal(2, report.ByPaymentMethod.Count);
        Assert.Equal("paypal", report.ByPaymentMethod[0].Key);
        Assert.Equal(15.00m, report.ByPaymentMethod[0].Revenue);
        Assert.Equal(1, report.ByPaymentMethod[0].Orders);
        Assert.Contains(report.ByPaymentMethod, p => p.Key == "credit_card" && p.Revenue == 8.00m);

        // Shipping breakdown.
        Assert.Contains(report.ByShippingMethod, s => s.Key == "standard" && s.Revenue == 15.00m);
        Assert.Contains(report.ByShippingMethod, s => s.Key == "express" && s.Revenue == 8.00m);

        // Top items — aspirin leads with 4 units (2+2) and 18.00 revenue (10+8).
        var top = report.TopItems[0];
        Assert.Equal("sku-asp", top.ItemId);
        Assert.Equal(4, top.Units);
        Assert.Equal(18.00m, top.Revenue);
        Assert.Contains(report.TopItems, i => i.ItemId == "sku-vit" && i.Units == 1 && i.Revenue == 5.00m);

        // Prescription activity is always emitted for the curated step set; transfer == 1, rest 0.
        Assert.Equal(4, report.Prescriptions.Count);
        Assert.Contains(report.Prescriptions, p => p.Key == "prescription_transfer" && p.Count == 1);
        Assert.Contains(report.Prescriptions, p => p.Key == "prescription_cart_add" && p.Count == 0);
    }

    [Fact]
    public async Task Sales_report_honours_platform_scope()
    {
        var now = DateTimeOffset.UtcNow;
        var at = now.AddDays(-1);
        await WriteAsync("android", Purchase("and1", at, "10.00", "standard", "paypal",
            new RSCAnalyticsItem("sku-1", "Thing", "cat", 10.00m, 1, "EUR")), at);

        var from = now.AddDays(-5);
        var until = now.AddDays(1);

        var android = await _store.GetSalesReportAsync(from, until, RSCAnalyticsScope.ForPlatform("android"), 10, default);
        Assert.Equal(1, android.OrderCount);
        Assert.Equal(10.00m, android.TotalRevenue);

        var ios = await _store.GetSalesReportAsync(from, until, RSCAnalyticsScope.ForPlatform("ios"), 10, default);
        Assert.Equal(0, ios.OrderCount);
        Assert.Equal(0m, ios.TotalRevenue);
        Assert.Empty(ios.TopItems);
    }

    [Fact]
    public async Task Sales_report_is_empty_when_no_purchases()
    {
        var now = DateTimeOffset.UtcNow;
        var report = await _store.GetSalesReportAsync(now.AddDays(-5), now.AddDays(1), RSCAnalyticsScope.All, 10, default);
        Assert.Equal(0, report.OrderCount);
        Assert.Equal(0m, report.TotalRevenue);
        Assert.Empty(report.ByDay);
        Assert.Empty(report.TopItems);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
