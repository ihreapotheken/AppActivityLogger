namespace ReportService.Admin.ViewModels;

/// <summary>
/// Compact analytics over the currently-filtered problem-report set, shown above the listing.
/// Computed in-memory from the matched rows (capped) so it stays consistent with whatever filter
/// the operator applied. <see cref="Truncated"/> is true when the match count exceeded the cap and
/// the breakdown therefore reflects only the first <c>cap</c> rows (the headline total is exact).
/// </summary>
public sealed record RSAReportsSummary(
    int Total,
    int Android,
    int Ios,
    int Multipart,
    int Json,
    int WithAttachment,
    long TotalBytes,
    long AttachmentBytes,
    bool Truncated)
{
    public static readonly RSAReportsSummary Empty = new(0, 0, 0, 0, 0, 0, 0, 0, false);

    public static RSAReportsSummary From(IReadOnlyList<RSAReportRowVM> rows, int totalMatched, int cap)
    {
        int android = 0, ios = 0, multipart = 0, json = 0, withAtt = 0;
        long bytes = 0, attBytes = 0;
        foreach (var r in rows)
        {
            if (string.Equals(r.Platform, "android", StringComparison.OrdinalIgnoreCase)) android++;
            else if (string.Equals(r.Platform, "ios", StringComparison.OrdinalIgnoreCase)) ios++;
            if (string.Equals(r.Channel, "multipart", StringComparison.OrdinalIgnoreCase)) multipart++;
            else if (string.Equals(r.Channel, "json", StringComparison.OrdinalIgnoreCase)) json++;
            if (r.AttachmentFileName is not null) { withAtt++; attBytes += r.AttachmentSizeBytes ?? 0; }
            bytes += r.SizeBytes;
        }
        return new RSAReportsSummary(totalMatched, android, ios, multipart, json, withAtt, bytes, attBytes,
            Truncated: totalMatched > cap);
    }
}
