namespace ReportService.Admin.ViewModels;

/// <summary>Error-reporting dashboard: crash + error tiles, per-platform rows, top error types, recent reports.</summary>
public sealed record RSAErrorDashboardVM(
    int CrashesLast24h,
    int ErrorsLast24h,
    int AffectedUsers,
    IReadOnlyList<RSAErrorPlatformRowVM> Platforms,
    IReadOnlyList<RSATopErrorVM> TopErrors,
    IReadOnlyList<RSAErrorRatePointVM> ErrorRate,
    IReadOnlyList<RSARecentErrorVM> RecentErrors);

public sealed record RSAErrorPlatformRowVM(
    string Name,
    int CrashesLast24h,
    int ErrorsLast24h,
    int AffectedUsers);

/// <summary>
/// One row of the "top errors" table. <see cref="Signature"/> is the dedup key — either the
/// top stack frame (preferred, set at ingest from the gzip attachment) or the truncated message
/// when no stack trace is available. <see cref="MultipartCount"/> + <see cref="JsonCount"/> sum
/// to <see cref="Occurrences"/> and let the operator see at a glance which ingestion paths are
/// hitting this fault site.
/// </summary>
public sealed record RSATopErrorVM(
    string Signature,
    int Occurrences,
    int AffectedUsers,
    int MultipartCount,
    int JsonCount);

/// <summary>
/// One row of the "recent errors" feed. <see cref="Signature"/> is the same per-row identifier
/// used in the top-errors table, so an operator can correlate a recent occurrence with its
/// rolled-up bucket without a second lookup. <see cref="Channel"/> tags the ingestion path
/// (multipart / json) — surfaced as a small badge in the row.
/// </summary>
public sealed record RSARecentErrorVM(
    DateTimeOffset OccurredAt,
    string Platform,
    string Signature,
    string Channel);

/// <summary>
/// One "trending issue" for the dashboard: a crash fault site (top-frame signature) with its
/// occurrence count in the recent window vs the prior window of equal length, so an operator can
/// spot crashes that are spiking. <see cref="Platform"/> is the platform contributing the most
/// occurrences in the recent window; a trend is derived in the view from recent vs prior
/// (rising / falling / new this period).
/// </summary>
public sealed record RSATrendingIssueVM(
    string Signature,
    string Platform,
    int RecentCount,
    int PriorCount,
    int AffectedUsers);

/// <summary>
/// One bucket of the error-rate trend: a pre-formatted x-axis label and the crash+error count that
/// landed in that bucket. The service picks the bucket width (day / week / month) from the selected
/// range, so the view just plots Label → Count without re-deriving any dates.
/// </summary>
public sealed record RSAErrorRatePointVM(string Label, int Count);

/// <summary>Operator-selectable spans for the error-rate chart. <see cref="Custom"/> reads explicit
/// from/until dates; every other value is a rolling window ending "now".</summary>
public enum RSAErrorRateRange
{
    Last7Days,
    Last30Days,
    Last3Months,
    Last6Months,
    LastYear,
    Custom,
}

/// <summary>Bucket width the error-rate series is rolled up to. Chosen from the window span so a
/// long range doesn't render hundreds of daily dots.</summary>
public enum RSAErrorRateBucket
{
    Day,
    Week,
    Month,
}

/// <summary>
/// A resolved error-rate window: the half-open UTC interval <c>[FromUtc, ToUtc)</c> the chart covers
/// plus the <see cref="RSAErrorRateBucket"/> its points are rolled up to. Built from a
/// <see cref="RSAErrorRateRange"/> (or explicit custom dates) via <see cref="Resolve"/>.
/// </summary>
public sealed record RSAErrorRateWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc, RSAErrorRateBucket Bucket)
{
    /// <summary>Picks the bucket width from the span: daily up to ~a month, weekly up to ~6 months,
    /// monthly beyond — keeping the point count roughly bounded (≤ ~31 daily, ≤ ~27 weekly).</summary>
    public static RSAErrorRateWindow ForSpan(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var days = (toUtc - fromUtc).TotalDays;
        var bucket = days <= 31.0 ? RSAErrorRateBucket.Day
                   : days <= 184.0 ? RSAErrorRateBucket.Week
                   : RSAErrorRateBucket.Month;
        return new RSAErrorRateWindow(fromUtc, toUtc, bucket);
    }

    /// <summary>Resolves a range selection to a concrete UTC window. Rolling presets end at
    /// <paramref name="now"/> and include today; <see cref="RSAErrorRateRange.Custom"/> uses the
    /// supplied dates (end date inclusive), defaulting either bound and swapping if reversed.</summary>
    public static RSAErrorRateWindow Resolve(RSAErrorRateRange range, DateOnly? customFrom, DateOnly? customTo, DateTimeOffset now)
    {
        static DateTimeOffset Midnight(DateOnly d) => new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);

        if (range == RSAErrorRateRange.Custom)
        {
            var toDate = customTo ?? DateOnly.FromDateTime(now.UtcDateTime);
            var fromDate = customFrom ?? toDate.AddDays(-6);
            if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
            // ToUtc is exclusive, so push to the start of the day after the end date to include it.
            return ForSpan(Midnight(fromDate), Midnight(toDate).AddDays(1));
        }

        var days = range switch
        {
            RSAErrorRateRange.Last30Days => 30,
            RSAErrorRateRange.Last3Months => 90,
            RSAErrorRateRange.Last6Months => 180,
            RSAErrorRateRange.LastYear => 365,
            _ => 7,
        };
        var u = now.UtcDateTime;
        var todayMidnight = new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero);
        return ForSpan(todayMidnight.AddDays(-(days - 1)), now);
    }
}
