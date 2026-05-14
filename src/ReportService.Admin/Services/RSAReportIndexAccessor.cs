using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>Default accessor that holds an optional <see cref="RSCResilientReportIndex"/>.</summary>
internal sealed class RSAReportIndexAccessor : IRSAReportIndexAccessor
{
    private readonly RSCResilientReportIndex? _resilient;

    public RSAReportIndexAccessor(RSCResilientReportIndex? resilient = null)
    {
        _resilient = resilient;
    }

    public bool IsConfigured => _resilient is not null;
    public RSCResilientReportIndex? Resilient => _resilient;
    public RSCIReportIndexMaintenance? Maintenance =>
        _resilient?.TryGetInnerForMaintenance() as RSCIReportIndexMaintenance;
}
