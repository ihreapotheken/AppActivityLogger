using ReportService.Admin.Models;
using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Validation;

namespace ReportService.Admin.Services;

/// <summary>
/// Lists problem reports for the admin pages in the <b>database-per-app</b> model: it reads through the
/// per-app fan-out <see cref="RSCIReportStore"/> (which merges every app's own SQLite index) and scopes
/// the result to the selected <c>(client, app)</c>. The legacy single global index is no longer the
/// source of truth for per-app report data, so it is not read here. Filtering on columns carried by
/// <see cref="RSCStoredReport"/> (platform, kind, attachment, dates, top frame, ingestion channel,
/// file name, owning client/app) is honoured; the rarer index-only filters (pharmacy / email / user id
/// / app version) are not applied on this path — pushing them down per-app is a follow-up.
/// </summary>
internal sealed class RSAReportListingService : IRSAReportListingService
{
    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAReportListingService(
        RSCIReportStore store,
        RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    public Task<RSAReportsPageVM> ListAsync(
        RSAReportsFilterInput filter, int pageSize, RSAReportListingScope scope, CancellationToken ct)
    {
        var pageNumber = Math.Max(1, filter.Page);
        var offset = (pageNumber - 1) * pageSize;

        var canonicalPlatform = RSCPlatforms.TryCanonicalize(filter.Platform, _options);

        // Page-scope wins over the bound HasAttachment so the per-category page can guarantee its
        // invariant (e.g. ProblemReports always has an attachment) regardless of what the form sent.
        var hasAttachment = scope.RequireAttachment ?? filter.HasAttachment;

        // The operator's kind pick narrows the page's implicit kind scope (e.g. crash-only within
        // the /Errors crash+error scope). Resolved once here so every consumer of ListAsync — the
        // page, the on-page summary, and "delete matching" — applies the same effective scope.
        var kindIn = ResolveKindIn(filter.Kind, scope.KindIn);

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
            KindIn: kindIn,
            KindNotIn: scope.KindNotIn,
            TopFrame: NullIfWhitespace(filter.TopFrame),
            Limit: pageSize,
            Offset: offset);

        return Task.FromResult(ReadFromStore(dbFilter, pageNumber, pageSize, offset, scope.ClientId, scope.AppId));
    }

    /// <summary>
    /// Combines the page's implicit kind scope with the operator's optional kind pick. With no pick
    /// the page scope stands; a pick narrows to that single kind — but only when it falls inside the
    /// page scope (or the page has no kind scope at all). An out-of-scope pick is ignored so a
    /// hand-edited <c>kind=</c> query can't widen a listing past its page boundary.
    /// </summary>
    private static IReadOnlyList<string>? ResolveKindIn(string? pick, IReadOnlyList<string>? scopeKindIn)
    {
        if (string.IsNullOrWhiteSpace(pick)) return scopeKindIn;
        var k = pick.Trim();
        if (scopeKindIn is null || scopeKindIn.Contains(k, StringComparer.OrdinalIgnoreCase))
            return new[] { k };
        return scopeKindIn;
    }

    private RSAReportsPageVM ReadFromStore(RSCReportFilter f, int pageNumber, int pageSize, int offset,
        string? clientId = null, string? appId = null)
    {
        var platforms = f.Platform is null ? _options.AllowedPlatforms : new[] { f.Platform };
        var all = new List<RSCStoredReport>();
        foreach (var p in platforms) all.AddRange(_store.List(p));

        IEnumerable<RSCStoredReport> seq = all;
        // Database-per-app tenancy scope: the fan-out store stamps each row's owning (client, app);
        // restrict to the selected one when set (null = all apps). For a client login the (client)
        // axis is pinned upstream, so this is also the tenant-isolation boundary.
        if (!string.IsNullOrWhiteSpace(clientId))
            seq = seq.Where(r => string.Equals(r.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(appId))
            seq = seq.Where(r => string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(f.FileNameContains))
            seq = seq.Where(r => r.FileName.Contains(f.FileNameContains!, StringComparison.OrdinalIgnoreCase));
        if (f.HasAttachment is not null)
            seq = seq.Where(r => (r.AttachmentFileName is not null) == f.HasAttachment.Value);
        if (!string.IsNullOrWhiteSpace(f.IngestionChannel))
            seq = seq.Where(r => string.Equals(r.IngestionChannel, f.IngestionChannel, StringComparison.OrdinalIgnoreCase));
        if (f.SubmittedFrom is not null)
            seq = seq.Where(r => r.SubmittedAt >= f.SubmittedFrom);
        if (f.SubmittedUntil is not null)
            seq = seq.Where(r => r.SubmittedAt <= f.SubmittedUntil);
        if (!string.IsNullOrWhiteSpace(f.TopFrame))
            seq = seq.Where(r => string.Equals(r.TopFrame, f.TopFrame, StringComparison.Ordinal));
        // The disk walk doesn't see the kind column directly; RSCStoredReport.Kind is set by the
        // SQLite reader path. With the index degraded we apply best-effort kind filtering on the
        // values we have — rows with null Kind pass through (consistent with the SQL semantics).
        // f.KindIn already carries the resolved page-scope ∩ operator-pick from ResolveKindIn.
        if (f.KindIn is { Count: > 0 } kindIn)
            seq = seq.Where(r => r.Kind is { } k && kindIn.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (f.KindNotIn is { Count: > 0 } kindNotIn)
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
