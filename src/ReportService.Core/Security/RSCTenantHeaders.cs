namespace ReportService.Security;

/// <summary>
/// Canonical names for the optional request headers that let a caller (or a fronting gateway) pin
/// tenancy attribution — the client and app — without the SDK changing its JSON body. Defined once,
/// here, so the analytics and problem-report ingestion paths can never drift to mismatched or
/// clashing header strings.
/// <para>
/// Resolution precedence on ingestion is the same on both surfaces: a client-bound key's
/// <see cref="RSCTenantClaims.ClientId"/> claim wins outright for the client; otherwise the client
/// header wins over the body, which wins over the configured default. The app always resolves
/// header → body → default (the key never names the app). Environment is folded into the app slug,
/// so <see cref="AnalyticsEnvironment"/> is vestigial and never a tenancy axis.
/// </para>
/// </summary>
public static class RSCTenantHeaders
{
    // Analytics ingestion — POST /api/v2/analytics/events and /server-events.
    public const string AnalyticsClient = "X-Analytics-Client";
    public const string AnalyticsApp = "X-Analytics-App";
    public const string AnalyticsEnvironment = "X-Analytics-Environment";

    // Problem-report ingestion — POST /partners/api/v2/report-problem and /api/v1/reports.
    public const string ReportClient = "X-Report-Client";
    public const string ReportApp = "X-Report-App";
}
