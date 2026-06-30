namespace ReportService.Models;

/// <summary>
/// Shape of the <c>json</c> multipart part posted by the Android / iOS IA SDKs. Field names are on-the-wire.
/// The iOS CardLink integration duplicates the phone number in both <c>PhoneNumber</c> and <c>Phone</c>;
/// <c>FunctionalityImportance</c> is Android-only.
///
/// <para>The optional analytics fields (<c>Kind</c>, <c>StackTrace</c>, <c>EventProperties</c>,
/// <c>OccurredAt</c>) are populated by <c>AnalyticsRemoteDataSource</c> (Android) and
/// <c>URLSessionAnalyticsReporter</c> (iOS). <c>Kind</c> = <c>"analytics"</c> for tracking events,
/// <c>"crash"</c> for runtime errors. Unknown to the legacy multipart submission path — the
/// validator only enforces required fields.</para>
/// </summary>
public sealed record RSCProblemReport(
    string Platform,
    string Message,
    string? Title,
    string? DeviceModel,
    string? Email,
    string? PhoneNumber,
    string? Phone,
    string? PharmacyId,
    string? Source,
    string? AppVersion,
    string? FunctionalityImportance,
    IReadOnlyList<string>? Labels,
    string? Kind = null,
    string? StackTrace = null,
    IReadOnlyDictionary<string, string>? EventProperties = null,
    string? OccurredAt = null,
    string? UserId = null,
    // Tenancy attribution (database-per-app). appId/environment may be sent on the body (or the
    // X-Report-App / X-Report-Environment headers); clientId is derived server-side from the
    // authenticated API key, not trusted from the body. All optional so older SDK builds still parse
    // under UnmappedMemberHandling.Disallow.
    string? AppId = null,
    string? Environment = null,
    string? ClientId = null
);
