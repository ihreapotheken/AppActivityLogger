using ReportService.Options;

namespace ReportService.Validation;

/// <summary>Canonicalizes raw <c>platform</c> strings against <c>RSCReportServiceOptions.AllowedPlatforms</c> (itself stored lowercased).</summary>
public static class RSCPlatforms
{
    public static string? TryCanonicalize(string? raw, RSCReportServiceOptions options)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lower = raw.Trim().ToLowerInvariant();
        return Array.IndexOf(options.AllowedPlatforms, lower) >= 0 ? lower : null;
    }
}
