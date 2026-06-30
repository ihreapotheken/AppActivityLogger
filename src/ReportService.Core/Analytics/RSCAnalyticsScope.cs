namespace ReportService.Analytics;

/// <summary>
/// Tenancy + platform filter for analytics reads. Each component is an independent, parallel axis;
/// a <c>null</c> component means "all" for that axis (a null <see cref="AppId"/> returns every app,
/// etc.). An all-null scope (<see cref="All"/>) is the legacy "everything" query — that null-means-all
/// semantics is what keeps platform-only callers (and the pre-tenancy test suite) working unchanged.
/// </summary>
public readonly record struct RSCAnalyticsScope(string? AppId, string? Environment, string? ClientId, string? Platform)
{
    public static readonly RSCAnalyticsScope All = new(null, null, null, null);

    /// <summary>Scope by platform only (app/env/client unconstrained) — the pre-tenancy shape.</summary>
    public static RSCAnalyticsScope ForPlatform(string? platform) => new(null, null, null, platform);
}
