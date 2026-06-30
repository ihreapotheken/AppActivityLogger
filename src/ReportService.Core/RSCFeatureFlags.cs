namespace ReportService;

/// <summary>
/// Compile-time availability of the optional "Submissions" feature areas, decided by build flags in
/// the repo-root <c>Directory.Build.props</c> (e.g. <c>dotnet build -p:FeatureAnalytics=false</c>).
/// </summary>
/// <remarks>
/// A disabled feature is still compiled in — it is <b>gated</b>, not removed:
/// <list type="bullet">
///   <item>ingestion endpoints for the feature return <see cref="DisabledStatusCode"/> (503 Service
///         Unavailable) with <see cref="DisabledMessage"/> — the functionality is deliberately not
///         present in this build, which is a service-availability condition rather than an
///         unexpected server error (500);</item>
///   <item>admin pages render a "not enabled — contact your administrator" notice and the nav entry
///         is shown disabled, rather than 404/blank.</item>
/// </list>
/// The properties are deliberately NOT <c>const</c> so call sites like
/// <c>if (!RSCFeatureFlags.Analytics)</c> don't trip CS0162 (unreachable-code) when the flag is on.
/// </remarks>
public static class RSCFeatureFlags
{
    /// <summary>Whether the v2 analytics pipeline (ingestion + admin dashboards) is available.</summary>
    public static bool Analytics =>
#if FEATURE_ANALYTICS
        true;
#else
        false;
#endif

    /// <summary>Whether the problem-report (crash / user-report) pipeline is available.</summary>
    public static bool ProblemReports =>
#if FEATURE_PROBLEM_REPORTS
        true;
#else
        false;
#endif

    /// <summary>True iff <paramref name="feature"/> (case-insensitive name) is available. Lets the
    /// admin nav/pages gate by a string key without a switch at every call site.</summary>
    public static bool IsEnabled(string feature) => feature?.ToLowerInvariant() switch
    {
        "analytics" => Analytics,
        "problemreports" or "problem-reports" or "reports" => ProblemReports,
        _ => true,
    };

    /// <summary>Operator-facing message shown when a disabled feature is reached.</summary>
    public const string DisabledMessage =
        "This feature is not enabled in this build. Please contact your administrator.";

    /// <summary>HTTP status returned by a compiled-out feature's ingestion endpoints. 503 Service
    /// Unavailable: the route exists but this build deliberately does not serve it. Single source of
    /// truth — every feature gate references this so the status can be changed in one place.</summary>
    public const int DisabledStatusCode = 503;
}
