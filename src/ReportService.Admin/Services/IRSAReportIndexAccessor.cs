using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Typed seam for the optional <see cref="RSCResilientReportIndex"/>. The page model layer never
/// reaches into <see cref="IServiceProvider"/> to ask "do we have an index?" — instead it asks the
/// accessor for the maintenance surface and acts on the (possibly null) answer.
/// </summary>
/// <remarks>
/// The accessor is registered unconditionally: when <c>Storage != "SqliteIndex"</c> a
/// null-returning implementation is provided, which is preferable to forcing each page to know how
/// to construct its own conditional dependency. Compare with the prior pattern that called
/// <c>services.GetService(typeof(RSCResilientReportIndex))</c> in five different places.
/// </remarks>
public interface IRSAReportIndexAccessor
{
    /// <summary>True when the SQLite metadata index is wired up (regardless of current health).</summary>
    bool IsConfigured { get; }

    /// <summary>The resilient wrapper, or <c>null</c> when running in plain filesystem mode.</summary>
    RSCResilientReportIndex? Resilient { get; }

    /// <summary>The maintenance surface, or <c>null</c> when the index is unconfigured or currently degraded.</summary>
    RSCIReportIndexMaintenance? Maintenance { get; }
}
