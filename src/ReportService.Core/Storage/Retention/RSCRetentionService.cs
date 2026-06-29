using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReportService.Audit;
using ReportService.Options;

namespace ReportService.Storage.Retention;

/// <summary>
/// Enforces the storage retention policy: delete anything older than
/// <see cref="RSCReportServiceOptions.RetentionMaxAgeDays"/>, then if the total footprint still
/// exceeds <see cref="RSCReportServiceOptions.RetentionMaxBytes"/>, delete the oldest reports until
/// the store is back to ~95% of the cap (cushion to avoid thrashing on every ingest). Every sweep
/// writes one audit row summarising what was removed and why.
/// </summary>
/// <remarks>
/// All deletions go through <see cref="RSCIReportStore.Delete"/> so file + sibling attachment + index
/// row are removed atomically. Concurrent sweeps are serialised via an internal semaphore: one
/// sweep at a time, regardless of how many callers (background timer + admin button) hit it.
/// </remarks>
public sealed class RSCRetentionService
{
    /// <summary>
    /// After deleting, leave roughly this fraction of the cap free so the next ingest doesn't
    /// immediately re-trigger a sweep. 95% of the cap = 5% headroom.
    /// </summary>
    private const double SizeTargetFraction = 0.95;

    /// <summary>
    /// When the disk-pressure guard fires, free 10% more than the strict deficit so a sweep that
    /// just passed doesn't immediately re-trigger on the next tick. Proportional to the deficit so
    /// it stays small.
    /// </summary>
    private const double DiskFreeCushionFraction = 0.10;

    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;
    private readonly RSCIAuditLog _audit;
    private readonly ILogger<RSCRetentionService> _logger;
    private readonly RSCIDiskSpaceProbe _diskProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RSCRetentionService(
        RSCIReportStore store,
        RSCReportServiceOptions options,
        RSCIAuditLog audit,
        ILogger<RSCRetentionService> logger,
        RSCIDiskSpaceProbe diskProbe)
    {
        _store = store;
        _options = options;
        _audit = audit;
        _logger = logger;
        _diskProbe = diskProbe;
    }

    /// <summary>
    /// Runs one full sweep (age then size). <paramref name="actor"/> is recorded on the audit row;
    /// pass <c>"retention.background"</c> from the timer or the operator's identity for manual triggers.
    /// </summary>
    public async Task<RSCRetentionReport> SweepAsync(string actor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SweepCoreAsync(actor, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Read-only snapshot of the store's current usage. No deletions, no audit row.</summary>
    public RSCRetentionStats GetStats()
    {
        var (reports, totalBytes) = CollectAndSum();
        DateTimeOffset? oldest = reports.Count > 0 ? reports[0].SubmittedAt : null;
        DateTimeOffset? newest = reports.Count > 0 ? reports[^1].SubmittedAt : null;
        // Surface filesystem headroom on the dashboard regardless of whether the disk guard is
        // armed — it's the number that matters for "will this box run out of space?", and the byte
        // cap above only accounts for report blobs, not the DBs/backups sharing the volume.
        var disk = _diskProbe.Probe(_options.ReportsRoot);
        return new RSCRetentionStats(
            UsedBytes: totalBytes,
            LimitBytes: _options.RetentionMaxBytes,
            ReportCount: reports.Count,
            Oldest: oldest,
            Newest: newest,
            Enabled: _options.RetentionEnabled,
            MaxAgeDays: _options.RetentionMaxAgeDays,
            DiskTotalBytes: disk?.TotalBytes,
            DiskFreeBytes: disk?.FreeBytes);
    }

    private async Task<RSCRetentionReport> SweepCoreAsync(string actor, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var maxBytes = Math.Max(0, _options.RetentionMaxBytes);
        var maxAgeDays = Math.Max(0, _options.RetentionMaxAgeDays);

        if (!_options.RetentionEnabled || (maxBytes == 0 && maxAgeDays == 0))
        {
            // Nothing to do — both knobs disabled. Compute current size for the report and bail.
            var (_, currentBytes) = CollectAndSum();
            sw.Stop();
            return RSCRetentionReport.Disabled(currentBytes, maxBytes) with { Elapsed = sw.Elapsed };
        }

        var (reports, totalBefore) = CollectAndSum();
        if (reports.Count == 0)
        {
            sw.Stop();
            return new RSCRetentionReport(0, 0, 0, 0, 0, maxBytes, maxAgeDays > 0 ? maxAgeDays : null, DateTimeOffset.UtcNow, sw.Elapsed);
        }

        // Oldest first; same ordering used for both passes.
        reports.Sort((a, b) => a.SubmittedAt.CompareTo(b.SubmittedAt));

        var deletedByAge = 0;
        var deletedBySize = 0;
        long deletedBytes = 0;
        long currentTotal = totalBefore;

        // ----- Pass 1: age cutoff. Anything older than the cutoff goes regardless of cap. -----
        if (maxAgeDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays);
            for (var i = 0; i < reports.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var r = reports[i];
                if (r.SubmittedAt > cutoff) break; // sorted ascending — first young one means done

                var size = r.SizeBytes + (r.AttachmentSizeBytes ?? 0);
                if (_store.Delete(r.Platform, r.FileName))
                {
                    deletedByAge++;
                    deletedBytes += size;
                    currentTotal -= size;
                    reports[i] = r with { FileName = string.Empty }; // tombstone — skipped below
                }
            }
        }

        // ----- Pass 2: size cap. Delete remaining oldest until we hit the soft target. -----
        if (maxBytes > 0 && currentTotal > maxBytes)
        {
            var target = (long)(maxBytes * SizeTargetFraction);
            for (var i = 0; i < reports.Count; i++)
            {
                if (currentTotal <= target) break;
                if (ct.IsCancellationRequested) break;
                var r = reports[i];
                if (string.IsNullOrEmpty(r.FileName)) continue; // already deleted by age pass

                var size = r.SizeBytes + (r.AttachmentSizeBytes ?? 0);
                if (_store.Delete(r.Platform, r.FileName))
                {
                    deletedBySize++;
                    deletedBytes += size;
                    currentTotal -= size;
                    reports[i] = r with { FileName = string.Empty }; // tombstone for the disk pass
                }
                else
                {
                    // The row was listed but couldn't be deleted (index/disk drift, or an IOException
                    // in the file store). Its bytes are NOT reclaimable storage we can act on, so
                    // subtract them from the running total anyway — otherwise the loop keeps counting
                    // a stuck row toward usage and deletes the next (newer, in-policy) report to
                    // compensate, purging healthy data to make up for an undeletable one.
                    currentTotal -= size;
                    reports[i] = r with { FileName = string.Empty }; // can't retry it in the disk pass either
                    _logger.LogWarning(
                        "Retention size pass could not delete {Platform}/{File} ({Size} bytes); excluding it from the running total so it can't force deletion of newer reports",
                        r.Platform, r.FileName, size);
                }
            }
        }

        // ----- Pass 3: disk-pressure guard. Independent of the byte cap — it protects the actual
        // filesystem from growth the cap can't see (analytics/audit DBs, backups, un-VACUUMed slack,
        // or neighbours on a shared mount). Evicts oldest remaining blobs until free space / usage is
        // back under the threshold (plus a small cushion), or we run out of report blobs to delete. --
        var deletedByDisk = 0;
        var minFreeDisk = Math.Max(0, _options.RetentionMinFreeDiskBytes);
        var maxDiskPct = _options.RetentionMaxDiskUsagePercent is >= 1 and <= 99
            ? _options.RetentionMaxDiskUsagePercent
            : 0;
        if ((minFreeDisk > 0 || maxDiskPct > 0) && !ct.IsCancellationRequested)
        {
            var disk = _diskProbe.Probe(_options.ReportsRoot);
            if (disk is { } d && d.TotalBytes > 0)
            {
                var bytesToFree = DiskBytesToFree(d.TotalBytes, d.FreeBytes, minFreeDisk, maxDiskPct);
                if (bytesToFree > 0)
                {
                    long freedForDisk = 0;
                    for (var i = 0; i < reports.Count && freedForDisk < bytesToFree; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var r = reports[i];
                        if (string.IsNullOrEmpty(r.FileName)) continue; // deleted by an earlier pass

                        var size = r.SizeBytes + (r.AttachmentSizeBytes ?? 0);
                        if (_store.Delete(r.Platform, r.FileName))
                        {
                            deletedByDisk++;
                            deletedBytes += size;
                            currentTotal -= size;
                            freedForDisk += size;
                            reports[i] = r with { FileName = string.Empty };
                        }
                        else
                        {
                            // Same drift handling as the size pass: treat the stuck row as freed so it
                            // can't force deletion of newer reports.
                            freedForDisk += size;
                            currentTotal -= size;
                            reports[i] = r with { FileName = string.Empty };
                        }
                    }

                    if (freedForDisk < bytesToFree)
                    {
                        _logger.LogWarning(
                            "Retention disk guard freed {Freed} of {Need} bytes on the {Root} filesystem then ran out of report blobs to delete. " +
                            "Remaining pressure is from non-report data (analytics/audit DBs, backups, un-VACUUMed slack) — that needs a manual VACUUM/cleanup; deleting reports can't reclaim it.",
                            freedForDisk, bytesToFree, _options.ReportsRoot);
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Retention disk guard is armed but free space for {Root} could not be determined this sweep; skipping the disk pass",
                    _options.ReportsRoot);
            }
        }

        sw.Stop();

        var report = new RSCRetentionReport(
            DeletedByAge: deletedByAge,
            DeletedBySize: deletedBySize,
            DeletedBytes: deletedBytes,
            BytesBefore: totalBefore,
            BytesAfter: currentTotal,
            LimitBytes: maxBytes,
            MaxAgeDays: maxAgeDays > 0 ? maxAgeDays : null,
            At: DateTimeOffset.UtcNow,
            Elapsed: sw.Elapsed,
            DeletedByDisk: deletedByDisk);

        if (report.DidWork)
        {
            _logger.LogWarning(
                "Retention sweep deleted {Total} reports ({Bytes} bytes; {ByAge} aged, {BySize} oversize, {ByDisk} disk-pressure). Store {Before} → {After} bytes (cap {Cap}).",
                report.DeletedTotal, report.DeletedBytes, report.DeletedByAge, report.DeletedBySize, report.DeletedByDisk,
                report.BytesBefore, report.BytesAfter, maxBytes);

            await _audit.RecordAsync(new RSCAuditEntry(
                At: report.At,
                Actor: actor,
                RemoteAddress: "system",
                Action: "retention.sweep",
                Target: null,
                Details: $"deleted={report.DeletedTotal} (age={report.DeletedByAge}, size={report.DeletedBySize}, disk={report.DeletedByDisk}) " +
                         $"bytes={report.DeletedBytes} before={report.BytesBefore} after={report.BytesAfter} cap={maxBytes} max_age_days={maxAgeDays}",
                Success: true), ct).ConfigureAwait(false);
        }

        return report;
    }

    /// <summary>
    /// Bytes the disk pass must reclaim to satisfy whichever guard is breached (absolute free-bytes
    /// floor and/or max-usage percent), taking the larger of the two, plus a small cushion. Returns
    /// 0 when neither guard is breached.
    /// </summary>
    private static long DiskBytesToFree(long totalBytes, long freeBytes, long minFreeBytes, int maxUsagePercent)
    {
        long need = 0;

        if (minFreeBytes > 0 && freeBytes < minFreeBytes)
            need = Math.Max(need, minFreeBytes - freeBytes);

        if (maxUsagePercent > 0)
        {
            var maxUsedBytes = (long)(totalBytes * (maxUsagePercent / 100.0));
            var usedBytes = totalBytes - freeBytes;
            if (usedBytes > maxUsedBytes)
                need = Math.Max(need, usedBytes - maxUsedBytes);
        }

        return need <= 0 ? 0 : need + (long)(need * DiskFreeCushionFraction);
    }

    private (List<RSCStoredReport> Reports, long TotalBytes) CollectAndSum()
    {
        var all = new List<RSCStoredReport>();
        long total = 0;
        foreach (var platform in _options.AllowedPlatforms)
        {
            var rows = _store.List(platform);
            foreach (var r in rows)
            {
                all.Add(r);
                total += r.SizeBytes + (r.AttachmentSizeBytes ?? 0);
            }
        }

        // Sort by SubmittedAt for the stats getter — ascending so [0] is oldest, [^1] newest.
        all.Sort((a, b) => a.SubmittedAt.CompareTo(b.SubmittedAt));
        return (all, total);
    }
}
