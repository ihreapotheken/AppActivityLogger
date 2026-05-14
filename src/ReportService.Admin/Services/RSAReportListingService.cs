using ReportService.Admin.Models;
using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Validation;

namespace ReportService.Admin.Services;

/// <summary>
/// Reads from the SQLite metadata index when it's available (Storage=SqliteIndex) and falls back to
/// a per-platform disk walk otherwise. The fallback is deliberately partial — it cannot honour the
/// channel filter (the column is index-only) — but it preserves listing behaviour when the SQLite
/// surface is missing or degraded.
/// </summary>
internal sealed class RSAReportListingService : IRSAReportListingService
{
    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;
    private readonly IRSAReportIndexAccessor _indexAccessor;

    public RSAReportListingService(
        RSCIReportStore store,
        RSCReportServiceOptions options,
        IRSAReportIndexAccessor indexAccessor)
    {
        _store = store;
        _options = options;
        _indexAccessor = indexAccessor;
    }

    public Task<RSAReportsPageVM> ListAsync(RSAReportsFilterInput filter, int pageSize, CancellationToken ct)
        => ListAsync(filter, pageSize, new RSAReportListingScope(), ct);

    public async Task<RSAReportsPageVM> ListAsync(
        RSAReportsFilterInput filter, int pageSize, RSAReportListingScope scope, CancellationToken ct)
    {
        var pageNumber = Math.Max(1, filter.Page);
        var offset = (pageNumber - 1) * pageSize;

        var canonicalPlatform = RSCPlatforms.TryCanonicalize(filter.Platform, _options);

        // Page-scope wins over the bound HasAttachment so the per-category page can guarantee its
        // invariant (e.g. ProblemReports always has an attachment) regardless of what the form sent.
        var hasAttachment = scope.RequireAttachment ?? filter.HasAttachment;

        var dbFilter = new RSCReportFilter(
            Platform: canonicalPlatform,
            PharmacyId: NullIfWhitespace(filter.PharmacyId),
            UserId: NullIfWhitespace(filter.UserId),
            Email: NullIfWhitespace(filter.Email),
            Phone: NullIfWhitespace(filter.Phone),
            AppVersion: NullIfWhitespace(filter.AppVersion),
            HasAttachment: hasAttachment,
            FileNameContains: NullIfWhitespace(filter.Q),
            SubmittedFrom: filter.From is null ? null : ToUtc(filter.From.Value),
            SubmittedUntil: filter.Until is null ? null : ToUtc(filter.Until.Value),
            IngestionChannel: NullIfWhitespace(filter.Channel),
            KindIn: scope.KindIn,
            KindNotIn: scope.KindNotIn,
            TopFrame: NullIfWhitespace(filter.TopFrame),
            Limit: pageSize,
            Offset: offset);

        if (_indexAccessor.Maintenance is { } maint)
        {
            try
            {
                var page = await maint.SearchAsync(dbFilter, ct).ConfigureAwait(false);
                var rows = page.Items.Select(r => r.ToRow()).ToList();
                var totalPages = Math.Max(1, (int)Math.Ceiling(page.TotalMatched / (double)pageSize));
                return new RSAReportsPageVM(rows, page.TotalMatched, pageNumber, totalPages, UsedIndex: true);
            }
            catch
            {
                // Fall through to the disk path; the index health entry will surface the failure.
            }
        }

        return DiskFallback(dbFilter, pageNumber, pageSize, offset, scope);
    }

    private RSAReportsPageVM DiskFallback(RSCReportFilter f, int pageNumber, int pageSize, int offset, RSAReportListingScope scope)
    {
        var platforms = f.Platform is null ? _options.AllowedPlatforms : new[] { f.Platform };
        var all = new List<RSCStoredReport>();
        foreach (var p in platforms) all.AddRange(_store.List(p));

        IEnumerable<RSCStoredReport> seq = all;
        if (!string.IsNullOrWhiteSpace(f.FileNameContains))
            seq = seq.Where(r => r.FileName.Contains(f.FileNameContains!, StringComparison.OrdinalIgnoreCase));
        if (f.HasAttachment is not null)
            seq = seq.Where(r => (r.AttachmentFileName is not null) == f.HasAttachment.Value);
        if (f.SubmittedFrom is not null)
            seq = seq.Where(r => r.SubmittedAt >= f.SubmittedFrom);
        if (f.SubmittedUntil is not null)
            seq = seq.Where(r => r.SubmittedAt <= f.SubmittedUntil);
        if (!string.IsNullOrWhiteSpace(f.TopFrame))
            seq = seq.Where(r => string.Equals(r.TopFrame, f.TopFrame, StringComparison.Ordinal));
        // The disk walk doesn't see the kind column directly; RSCStoredReport.Kind is set by the
        // SQLite reader path. With the index degraded we apply best-effort kind filtering on the
        // values we have — rows with null Kind pass through (consistent with the SQL semantics).
        if (scope.KindIn is { Count: > 0 } kindIn)
            seq = seq.Where(r => r.Kind is { } k && kindIn.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (scope.KindNotIn is { Count: > 0 } kindNotIn)
            seq = seq.Where(r => r.Kind is null || !kindNotIn.Contains(r.Kind, StringComparer.OrdinalIgnoreCase));

        var list = seq.OrderByDescending(r => r.SubmittedAt).ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)pageSize));
        var rows = list.Skip(offset).Take(pageSize).Select(r => r.ToRow()).ToList();
        return new RSAReportsPageVM(rows, list.Count, pageNumber, totalPages, UsedIndex: false);
    }

    private static string? NullIfWhitespace(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static DateTimeOffset ToUtc(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
}
