using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Audit;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Retention;

namespace ReportService.Admin.Pages;

/// <summary>
/// Maintenance actions: rebuild the metadata index from files, run <c>PRAGMA integrity_check</c>,
/// run <c>VACUUM; ANALYZE</c>, export metadata as CSV/JSON, write a verified backup snapshot. Every
/// POST handler requires an authenticated cookie + antiforgery token and writes an audit entry
/// that reflects the action's success/failure.
/// </summary>
public sealed class RSAMaintenanceModel : PageModel
{
    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;
    private readonly IRSAReportIndexAccessor _indexAccessor;
    private readonly RSCIAuditLog _audit;
    private readonly RSCRetentionService _retention;
    private readonly RSAAnalyticsLegacyImporter _analyticsImporter;
    private readonly RSCIAnalyticsStore _analyticsStore;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly ILogger<RSAMaintenanceModel> _logger;

    public RSAMaintenanceModel(
        RSCIReportStore store,
        RSCReportServiceOptions options,
        IRSAReportIndexAccessor indexAccessor,
        RSCIAuditLog audit,
        RSCRetentionService retention,
        RSAAnalyticsLegacyImporter analyticsImporter,
        RSCIAnalyticsStore analyticsStore,
        RSCAnalyticsOptions analyticsOptions,
        ILogger<RSAMaintenanceModel> logger)
    {
        _store = store;
        _options = options;
        _indexAccessor = indexAccessor;
        _audit = audit;
        _retention = retention;
        _analyticsImporter = analyticsImporter;
        _analyticsStore = analyticsStore;
        _analyticsOptions = analyticsOptions;
        _logger = logger;
    }

    public int AnalyticsHashVersion => _analyticsOptions.IdentifierHashVersion;

    public bool IndexAvailable => _indexAccessor.Maintenance is not null;
    public string BackupRoot => RSCStatePaths.Resolve(_options.BackupRoot, _options.ReportsRoot);
    public IReadOnlyList<FileInfo> ExistingBackups { get; private set; } = Array.Empty<FileInfo>();
    public RSCRetentionStats Retention { get; private set; } = default!;

    public void OnGet()
    {
        Retention = _retention.GetStats();
        try
        {
            var dir = BackupRoot;
            if (Directory.Exists(dir))
            {
                ExistingBackups = new DirectoryInfo(dir).GetFiles("*.db")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(10)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list existing backups");
        }
    }

    public async Task<IActionResult> OnPostRebuildAsync(CancellationToken ct)
    {
        if (RequireMaintenance() is not { } maint)
        {
            await _audit.RecordAsync(HttpContext, "index.rebuild", success: false, details: "index unavailable");
            return FailWith("Rebuild", "index unavailable");
        }

        try
        {
            var report = await maint.RebuildAsync(_store, _options.AllowedPlatforms, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "index.rebuild", success: true,
                details: $"inserted={report.Inserted} stale_removed={report.StaleRemoved} files_on_disk={report.FilesOnDisk} elapsed={report.Elapsed.TotalMilliseconds:0}ms");
            TempData["Flash"] = $"Rebuild done: {report.Inserted} inserted, {report.StaleRemoved} stale rows removed ({report.FilesOnDisk} files scanned in {report.Elapsed.TotalMilliseconds:0}ms).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index rebuild failed");
            await _audit.RecordAsync(HttpContext, "index.rebuild", success: false, details: ex.Message);
            TempData["Flash"] = "Rebuild failed — see logs.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostIntegrityAsync(CancellationToken ct)
    {
        if (RequireMaintenance() is not { } maint)
        {
            await _audit.RecordAsync(HttpContext, "index.integrity", success: false, details: "index unavailable");
            return FailWith("IntegrityCheck", "index unavailable");
        }

        try
        {
            var result = await maint.IntegrityCheckAsync(ct).ConfigureAwait(false);
            var ok = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            await _audit.RecordAsync(HttpContext, "index.integrity", success: ok, details: result);
            TempData["Flash"] = ok ? "Integrity check: ok." : $"Integrity check: {result}";
        }
        catch (Exception ex)
        {
            await _audit.RecordAsync(HttpContext, "index.integrity", success: false, details: ex.Message);
            TempData["Flash"] = "Integrity check failed — see logs.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVacuumAsync(CancellationToken ct)
    {
        if (RequireMaintenance() is not { } maint)
        {
            await _audit.RecordAsync(HttpContext, "index.vacuum", success: false, details: "index unavailable");
            return FailWith("Vacuum", "index unavailable");
        }

        try
        {
            await maint.VacuumAsync(ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "index.vacuum", success: true);
            TempData["Flash"] = "VACUUM + ANALYZE complete.";
        }
        catch (Exception ex)
        {
            await _audit.RecordAsync(HttpContext, "index.vacuum", success: false, details: ex.Message);
            TempData["Flash"] = "Vacuum failed — see logs.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBackupAsync(CancellationToken ct)
    {
        if (RequireMaintenance() is not { } maint)
        {
            await _audit.RecordAsync(HttpContext, "index.backup", success: false, details: "index unavailable");
            return FailWith("Backup", "index unavailable");
        }
        if (_indexAccessor.Resilient?.TryGetInnerForMaintenance() is not RSCSqliteReportIndex si)
        {
            await _audit.RecordAsync(HttpContext, "index.backup", success: false, details: "maintenance surface unavailable");
            return FailWith("Backup", "maintenance surface unavailable");
        }

        var dest = si.DefaultBackupPath();
        try
        {
            await maint.BackupAsync(dest, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "index.backup", success: true, target: dest);
            TempData["Flash"] = $"Backup written to {dest}.";
        }
        catch (Exception ex)
        {
            await _audit.RecordAsync(HttpContext, "index.backup", success: false, target: dest, details: ex.Message);
            TempData["Flash"] = "Backup failed — see logs.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostExportAsync(string format, CancellationToken ct)
    {
        format = (format ?? "csv").ToLowerInvariant();
        if (format != "csv" && format != "json") format = "csv";

        var all = new List<RSCStoredReport>();
        foreach (var p in _options.AllowedPlatforms)
        {
            all.AddRange(_store.List(p));
        }

        await _audit.RecordAsync(HttpContext, "reports.export", success: true, details: $"format={format} rows={all.Count}");

        if (format == "json")
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(all);
            return File(bytes, "application/json", $"reports-metadata-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        }

        var sb = new StringBuilder();
        sb.AppendLine("platform,file_name,size_bytes,submitted_at,attachment_file_name,attachment_size_bytes");
        foreach (var r in all)
        {
            sb.Append(Csv(r.Platform)).Append(',');
            sb.Append(Csv(r.FileName)).Append(',');
            sb.Append(r.SizeBytes).Append(',');
            sb.Append(r.SubmittedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(r.AttachmentFileName ?? "")).Append(',');
            sb.Append(r.AttachmentSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "");
            sb.AppendLine();
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"reports-metadata-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    public async Task<IActionResult> OnPostPurgeAsync(CancellationToken ct)
    {
        var actor = User.Identity?.Name is { Length: > 0 } name ? $"admin:{name}" : "admin:anonymous";
        try
        {
            var report = await _retention.SweepAsync(actor, ct).ConfigureAwait(false);
            // RSCRetentionService writes its own audit row when DidWork. Record a "no-op" audit only
            // when the operator clicked Purge but nothing matched, so the action is still traceable.
            if (!report.DidWork)
            {
                await _audit.RecordAsync(HttpContext, "retention.sweep", success: true,
                    details: $"no-op bytes={report.BytesAfter} cap={report.LimitBytes}");
            }
            TempData["Flash"] = report.DidWork
                ? $"Retention sweep deleted {report.DeletedTotal} reports ({report.DeletedByAge} aged out, {report.DeletedBySize} oversize), freed {RSAByteFormatter.Format(report.DeletedBytes)}."
                : $"Retention sweep: nothing to do (using {RSAByteFormatter.Format(report.BytesAfter)} of {RSAByteFormatter.Format(report.LimitBytes)} cap).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual retention sweep failed");
            await _audit.RecordAsync(HttpContext, "retention.sweep", success: false, details: ex.Message);
            TempData["Flash"] = "Retention sweep failed — see logs.";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// Operator-triggered total wipe — deletes every stored problem report (JSON + gzip
    /// attachment) on every platform, plus every row in the forced-report allow-list. The audit
    /// log is intentionally preserved so the wipe itself stays traceable. Guarded by a confirmation
    /// token: the form must include `confirm=WIPE` or the handler refuses.
    /// </summary>
    public async Task<IActionResult> OnPostWipeAllAsync([FromForm] string? confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, "WIPE", StringComparison.Ordinal))
        {
            await _audit.RecordAsync(HttpContext, "store.wipe-all", success: false, details: "missing or wrong confirm token");
            TempData["Flash"] = "Wipe refused: type WIPE in the confirmation field to proceed.";
            return RedirectToPage();
        }

        var deletedReports = 0;
        long deletedBytes = 0;
        try
        {
            foreach (var platform in _options.AllowedPlatforms)
            {
                // Snapshot the listing BEFORE deleting; deleting through the store mutates the
                // backing list, and an in-place enumeration would skip every other entry.
                var snapshot = _store.List(platform).ToList();
                foreach (var stored in snapshot)
                {
                    if (_store.Delete(platform, stored.FileName))
                    {
                        deletedReports++;
                        deletedBytes += stored.SizeBytes + (stored.AttachmentSizeBytes ?? 0);
                    }
                }
            }

            // Forced-report allow-list lives in the same SQLite file but isn't covered by the
            // file-store delete loop above; clear it explicitly. The schema migration recreates
            // the table on next startup if it ever gets dropped, so a TRUNCATE-equivalent is fine.
            var forcedStore = HttpContext.RequestServices.GetService<RSCIForcedReportStore>();
            var forcedRemoved = 0;
            if (forcedStore is not null)
            {
                var entries = await forcedStore.ListAsync(ct).ConfigureAwait(false);
                foreach (var e in entries)
                {
                    if (await forcedStore.RemoveAsync(e.Id, ct).ConfigureAwait(false)) forcedRemoved++;
                }
            }

            await _audit.RecordAsync(HttpContext, "store.wipe-all", success: true,
                details: $"reports={deletedReports} bytes={deletedBytes} forced_rows={forcedRemoved}");
            TempData["Flash"] = $"Wiped {deletedReports} reports ({RSAByteFormatter.Format(deletedBytes)}) and {forcedRemoved} forced-report rows.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wipe-all failed after deleting {Count} reports / {Bytes} bytes", deletedReports, deletedBytes);
            await _audit.RecordAsync(HttpContext, "store.wipe-all", success: false,
                details: $"deleted={deletedReports} bytes={deletedBytes} error={ex.Message}");
            TempData["Flash"] = $"Wipe failed after deleting {deletedReports} reports — see logs.";
        }
        return RedirectToPage();
    }

    private const long MaxRestoreBytes = 200L * 1024 * 1024;
    private static readonly byte[] SqliteHeaderBytes = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public async Task<IActionResult> OnGetDownloadBackupAsync(string name, CancellationToken ct)
    {
        // Resolve the requested name against BackupRoot and refuse anything that escapes (..),
        // is absolute, or contains a path separator — matches the same hardening as the
        // /api/problem-reports download path.
        if (string.IsNullOrEmpty(name)
            || name.Contains('/') || name.Contains('\\')
            || Path.IsPathRooted(name))
        {
            return NotFound();
        }

        var root = Path.GetFullPath(BackupRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, name));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !candidate.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(candidate))
        {
            return NotFound();
        }

        await _audit.RecordAsync(HttpContext, "index.backup.download", success: true, target: name).ConfigureAwait(false);
        var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "application/x-sqlite3", name);
    }

    public async Task<IActionResult> OnPostRestoreAsync(IFormFile? file, CancellationToken ct)
    {
        if (RequireMaintenance() is not { } maint)
        {
            await _audit.RecordAsync(HttpContext, "index.restore", success: false, details: "index unavailable");
            return FailWith("Restore", "index unavailable");
        }
        if (file is null || file.Length == 0)
        {
            TempData["Flash"] = "Restore: no file selected.";
            return RedirectToPage();
        }
        if (file.Length > MaxRestoreBytes)
        {
            TempData["Flash"] = $"Restore: file is too large (>{MaxRestoreBytes / (1024 * 1024)} MiB).";
            return RedirectToPage();
        }

        // Resolve the live SQLite path through the maintenance status surface (avoids depending on
        // RSCSqliteReportIndex internals). The file is uploaded to a sibling staging path so a
        // failed validation never overwrites the live database.
        var status = await maint.GetStatusAsync(ct).ConfigureAwait(false);
        var livePath = status.DbPath;
        var staging = livePath + ".restore." + Guid.NewGuid().ToString("N");

        try
        {
            await using (var fs = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            // SQLite header sanity-check: first 16 bytes must be "SQLite format 3\0".
            var header = new byte[16];
            await using (var fs = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var read = await fs.ReadAsync(header.AsMemory(0, 16), ct).ConfigureAwait(false);
                if (read != 16 || !header.AsSpan().SequenceEqual(SqliteHeaderBytes))
                {
                    throw new InvalidOperationException("not a SQLite database file (header mismatch)");
                }
            }

            // Open the staging file read-only and run integrity_check to catch a corrupt upload
            // before we touch the live DB.
            var verifyConnString = new SqliteConnectionStringBuilder
            {
                DataSource = staging,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();
            using (var verify = new SqliteConnection(verifyConnString))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var result = cmd.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"integrity_check failed: {result}");
                }
            }

            // Drop pooled connections so the next operation reopens against the swapped file.
            // Then delete the WAL/SHM sidecars (they'd be inconsistent with the new main DB) and
            // atomically move the staged file over the live path.
            SqliteConnection.ClearAllPools();
            TryDelete(livePath + "-wal");
            TryDelete(livePath + "-shm");
            System.IO.File.Move(staging, livePath, overwrite: true);

            await _audit.RecordAsync(HttpContext, "index.restore", success: true,
                target: file.FileName,
                details: $"size={file.Length} bytes={new FileInfo(livePath).Length}").ConfigureAwait(false);
            _logger.LogWarning("Operator {Operator} restored SQLite index from {File} ({Bytes} bytes)",
                User?.Identity?.Name, file.FileName, file.Length);
            TempData["Flash"] = $"Restore complete from {file.FileName}. Run an integrity check to confirm.";
        }
        catch (Exception ex)
        {
            TryDelete(staging);
            _logger.LogError(ex, "Restore failed for {File}", file.FileName);
            await _audit.RecordAsync(HttpContext, "index.restore", success: false,
                target: file.FileName, details: ex.Message).ConfigureAwait(false);
            TempData["Flash"] = $"Restore failed: {ex.Message}";
        }
        return RedirectToPage();

        static void TryDelete(string p)
        {
            try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { /* best-effort */ }
        }
    }

    public async Task<IActionResult> OnPostRotatePepperAsync([FromForm] string? confirm, CancellationToken ct)
    {
        // The pepper rotation here is *retrospective cleanup*. The actual pepper value lives in
        // Analytics:IdentifierHashPepper and is read at startup; the operator must update config and
        // restart the service before triggering this action. What we do here is purge orphaned
        // user-day rows: rollups under the old hash version can no longer be merged with the new
        // hashes (raw IDs were never stored), so they're discarded.
        //
        // Guarded by a confirm token so the destructive purge cannot fire accidentally.
        if (!string.Equals(confirm, "ROTATE", StringComparison.Ordinal))
        {
            await _audit.RecordAsync(HttpContext, "analytics.rotate-pepper", success: false,
                details: "missing or wrong confirm token");
            TempData["Flash"] = "Pepper rotation refused: type ROTATE in the confirmation field to proceed.";
            return RedirectToPage();
        }

        try
        {
            var purged = await _analyticsStore.PurgeUserDaysBelowHashVersionAsync(
                _analyticsOptions.IdentifierHashVersion, ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "analytics.rotate-pepper", success: true,
                details: $"purged_user_days={purged} new_hash_version={_analyticsOptions.IdentifierHashVersion}");
            TempData["Flash"] =
                $"Pepper rotation complete: purged {purged} stale user-day rows. Cohorts will start fresh under hash_version={_analyticsOptions.IdentifierHashVersion}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pepper rotation purge failed");
            await _audit.RecordAsync(HttpContext, "analytics.rotate-pepper", success: false, details: ex.Message);
            TempData["Flash"] = "Pepper rotation failed — see logs.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportAnalyticsAsync(CancellationToken ct)
    {
        try
        {
            var report = await _analyticsImporter.ImportAsync(ct).ConfigureAwait(false);
            await _audit.RecordAsync(HttpContext, "analytics.legacy-import", success: true,
                details: $"scanned={report.Scanned} converted={report.Converted} skipped={report.Skipped} failed={report.Failed}");
            TempData["Flash"] =
                $"Legacy analytics import: scanned {report.Scanned}, converted {report.Converted}, skipped {report.Skipped} non-analytics, {report.Failed} failed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy analytics import failed");
            await _audit.RecordAsync(HttpContext, "analytics.legacy-import", success: false, details: ex.Message);
            TempData["Flash"] = "Legacy analytics import failed — see logs.";
        }
        return RedirectToPage();
    }

    private RSCIReportIndexMaintenance? RequireMaintenance() => _indexAccessor.Maintenance;

    private IActionResult FailWith(string action, string reason)
    {
        _logger.LogWarning("Maintenance {Action} refused: {Reason}", action, reason);
        TempData["Flash"] = $"{action} is unavailable ({reason}).";
        return RedirectToPage();
    }

    private static string Csv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
