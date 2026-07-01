using ReportService.Storage;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// View-side projection of a stored report. The page templates bind to this DTO instead of the
/// storage-layer <c>RSCStoredReport</c>, so a column rename in the persistence layer does not
/// silently break Razor binding.
/// </summary>
public sealed record RSAReportRowVM(
    string Platform,
    string FileName,
    DateTimeOffset SubmittedAt,
    long SizeBytes,
    string? AttachmentFileName,
    long? AttachmentSizeBytes,
    string Channel,
    string ChannelLabel,
    string? Kind = null,
    string? TopFrame = null,
    RSCAttachmentLogSummary? LogSummary = null,
    string? ClientId = null,
    string? AppId = null);

/// <summary>
/// Maps storage records to <see cref="RSAReportRowVM"/> view rows. Co-located with the DTO it
/// produces. Centralised so the channel-label rule (null/empty → "multipart" because the file-system
/// fallback can't recover the column) lives in exactly one place.
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
            LogSummary: summary,
            ClientId: r.ClientId,
            AppId: r.AppId);
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
