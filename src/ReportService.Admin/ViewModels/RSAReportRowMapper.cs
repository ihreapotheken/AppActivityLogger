using ReportService.Storage;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Maps storage records to view-row DTOs. Centralised so the channel-label rule (null/empty →
/// "multipart" because the file-system fallback can't recover the column) lives in exactly one
/// place.
/// </summary>
internal static class RSAReportRowMapper
{
    public static RSAReportRowVM ToRow(this RSCStoredReport r)
    {
        var (channel, label) = ResolveChannel(r.IngestionChannel);
        RSCAttachmentLogSummary? summary = null;
        if (!string.IsNullOrEmpty(r.LogSummaryJson))
        {
            try
            {
                summary = System.Text.Json.JsonSerializer.Deserialize<RSCAttachmentLogSummary>(
                    r.LogSummaryJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            catch { /* tolerate stale or malformed blobs; row still renders without the summary */ }
        }
        return new RSAReportRowVM(
            Platform: r.Platform,
            FileName: r.FileName,
            SubmittedAt: r.SubmittedAt,
            SizeBytes: r.SizeBytes,
            AttachmentFileName: r.AttachmentFileName,
            AttachmentSizeBytes: r.AttachmentSizeBytes,
            Channel: channel,
            ChannelLabel: label,
            Kind: r.Kind,
            TopFrame: r.TopFrame,
            LogSummary: summary);
    }

    public static (string Channel, string Label) ResolveChannel(string? raw)
    {
        if (string.Equals(raw, RSCIngestionChannels.Json, StringComparison.Ordinal))
        {
            return (RSCIngestionChannels.Json, "JSON");
        }
        // Includes null (filesystem fallback) and "multipart" — both go in the same bucket.
        return (RSCIngestionChannels.Multipart, "multipart (zip)");
    }
}
