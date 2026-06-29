using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ReportService.Models;
using ReportService.Options;
using ReportService.Security;
using ReportService.Validation;

namespace ReportService.Storage;

/// <summary>
/// <see cref="RSCIReportStore"/> implementation that writes problem reports to the local file system under
/// <see cref="RSCReportServiceOptions.ReportsRoot"/>. For each platform it owns a
/// <c>&lt;ReportsRoot&gt;/&lt;platform&gt;/problem-reports/</c> folder. The JSON document and, when
/// present, its gzip attachment share a deterministic base name that includes hashes of both parts
/// — so identical-JSON-but-different-attachment uploads land under distinct paths instead of
/// overwriting each other.
/// </summary>
public sealed class RSCFileSystemReportStore : RSCIReportStore
{
    private const string ProblemReportsFolder = "problem-reports";

    private readonly RSCReportServiceOptions _options;
    private readonly ILogger<RSCFileSystemReportStore> _logger;

    public RSCFileSystemReportStore(RSCReportServiceOptions options, ILogger<RSCFileSystemReportStore> logger)
    {
        _options = options;
        _logger = logger;

        Directory.CreateDirectory(_options.ReportsRoot);
        foreach (var p in _options.AllowedPlatforms)
        {
            Directory.CreateDirectory(Path.Combine(_options.ReportsRoot, p, ProblemReportsFolder));
        }
    }

    /// <inheritdoc />
    public async Task<RSCStoredReport> SaveAsync(
        RSCProblemReport report,
        ReadOnlyMemory<byte> jsonBytes,
        Stream? attachment,
        long? attachmentLength,
        string ingestionChannel,
        CancellationToken ct)
    {
        // The file store is opaque to the channel — the column lives in the SQLite index. Argument
        // is accepted to satisfy the interface so the indexing decorator can pass it down.
        _ = ingestionChannel;
        var platform = report.Platform.ToLowerInvariant();
        var platformFolder = ResolvePlatformFolder(platform);

        var submittedAt = DateTimeOffset.UtcNow;
        var jsonHash12 = ShortHashHex(jsonBytes.Span);

        // Stream the attachment (if any) to a temp file while computing its SHA-256 in the same
        // pass. We need the digest to incorporate into the final filename, so different attachments
        // with identical JSON don't collide on a shared path — the class-level comment explains why
        // JSON-only idempotency was wrong.
        string? attachmentTempPath = null;
        string? attachmentHash12 = null;
        long attachmentSize = 0;

        if (attachment is not null)
        {
            attachmentTempPath = Path.Combine(platformFolder, $".incoming.{Guid.NewGuid():N}.log.gz");
            try
            {
                (attachmentHash12, attachmentSize) = await StreamAndHashAsync(attachmentTempPath, attachment, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(attachmentTempPath);
                throw;
            }
        }

        var ts = submittedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = attachmentHash12 is null
            ? $"problem-report_{ts}_{jsonHash12}"
            : $"problem-report_{ts}_{jsonHash12}_{attachmentHash12}";

        var jsonFileName = baseName + ".json";
        if (!RSCSafePath.TryCombine(platformFolder, jsonFileName, out var jsonFullPath))
        {
            TryDelete(attachmentTempPath);
            throw new InvalidOperationException("problem report JSON path could not be resolved safely");
        }

        try
        {
            await WriteBytesAtomicallyAsync(jsonFullPath, jsonBytes, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(attachmentTempPath);
            throw;
        }
        var jsonSize = new FileInfo(jsonFullPath).Length;

        string? attachmentFileName = null;
        long? storedAttachmentSize = null;
        if (attachmentTempPath is not null)
        {
            attachmentFileName = baseName + ".log.gz";
            if (!RSCSafePath.TryCombine(platformFolder, attachmentFileName, out var attachmentFullPath))
            {
                TryDelete(attachmentTempPath);
                TryDelete(jsonFullPath);
                throw new InvalidOperationException("problem report attachment path could not be resolved safely");
            }
            try
            {
                // The JSON was written first (above), so a failed attachment promotion would otherwise
                // leak the .incoming.<guid>.log.gz temp (invisible to List(), never reaped) AND orphan
                // the JSON (a report whose attachment never lands). Mirror the JSON path's cleanup:
                // drop both the temp attachment and the already-written JSON, then rethrow.
                File.Move(attachmentTempPath, attachmentFullPath, overwrite: true);
            }
            catch
            {
                TryDelete(attachmentTempPath);
                TryDelete(jsonFullPath);
                throw;
            }
            storedAttachmentSize = attachmentSize;

            _logger.LogInformation(
                "Persisted problem report {File} ({JsonBytes} bytes) + attachment {Attachment} ({AttachmentBytes} bytes)",
                jsonFileName, jsonSize, attachmentFileName, storedAttachmentSize);
        }
        else
        {
            _logger.LogInformation(
                "Persisted problem report {File} ({JsonBytes} bytes)", jsonFileName, jsonSize);
        }

        return new RSCStoredReport(
            Platform: platform,
            FileName: jsonFileName,
            SizeBytes: jsonSize,
            SubmittedAt: submittedAt,
            AttachmentFileName: attachmentFileName,
            AttachmentSizeBytes: storedAttachmentSize);
    }

    /// <inheritdoc />
    public IReadOnlyList<RSCStoredReport> List(string platform)
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return Array.Empty<RSCStoredReport>();

        var folder = Path.Combine(_options.ReportsRoot, p, ProblemReportsFolder);
        if (!Directory.Exists(folder)) return Array.Empty<RSCStoredReport>();

        var results = new List<RSCStoredReport>();
        foreach (var path in Directory.EnumerateFiles(folder, "problem-report_*.json", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            var baseName = Path.GetFileNameWithoutExtension(info.Name);
            var attachmentPath = Path.Combine(folder, baseName + ".log.gz");

            string? attachmentFileName = null;
            long? attachmentSize = null;
            if (File.Exists(attachmentPath))
            {
                var attachmentInfo = new FileInfo(attachmentPath);
                attachmentFileName = attachmentInfo.Name;
                attachmentSize = attachmentInfo.Length;
            }

            // Prefer the timestamp baked into the filename so SubmittedAt survives filesystem
            // operations (backup/restore, rsync, chmod) that mutate LastWriteTime.
            var submittedAt = TryParseSubmittedAt(info.Name, out var fromName)
                ? fromName
                : new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            results.Add(new RSCStoredReport(
                Platform: p,
                FileName: info.Name,
                SizeBytes: info.Length,
                SubmittedAt: submittedAt,
                AttachmentFileName: attachmentFileName,
                AttachmentSizeBytes: attachmentSize));
        }

        results.Sort(static (a, b) => b.SubmittedAt.CompareTo(a.SubmittedAt));
        return results;
    }

    /// <inheritdoc />
    public Stream? OpenRead(string platform, string fileName)
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return null;

        var folder = Path.Combine(_options.ReportsRoot, p, ProblemReportsFolder);
        Directory.CreateDirectory(folder);

        if (!RSCSafePath.TryCombine(folder, fileName, out var fullPath)) return null;
        return File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    /// <inheritdoc />
    // Attachment deletes are best-effort; only the JSON delete is authoritative.
    public bool Delete(string platform, string fileName)
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return false;

        var folder = Path.Combine(_options.ReportsRoot, p, ProblemReportsFolder);
        if (!RSCSafePath.TryCombine(folder, fileName, out var jsonFullPath)) return false;
        if (!File.Exists(jsonFullPath)) return false;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var attachmentName = baseName + ".log.gz";
        if (RSCSafePath.TryCombine(folder, attachmentName, out var attachmentFullPath) && File.Exists(attachmentFullPath))
        {
            try
            {
                File.Delete(attachmentFullPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete attachment {Attachment}", attachmentName);
            }
        }

        try
        {
            File.Delete(jsonFullPath);
            _logger.LogInformation("Deleted problem report {Platform}/{File}", p, fileName);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to delete report {Platform}/{File}", p, fileName);
            return false;
        }
    }

    private string ResolvePlatformFolder(string platformLower)
    {
        var folder = Path.Combine(_options.ReportsRoot, platformLower, ProblemReportsFolder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // Per-writer .tmp.<guid> + atomic rename. GUID-suffix avoids collisions when two writers
    // target the same final path concurrently.
    private static async Task WriteBytesAtomicallyAsync(string fullPath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var tempPath = $"{fullPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    // Streams + hashes in one pass. Returns the 12-hex prefix of SHA-256(source) + written byte count;
    // caller renames the temp into place once the final filename is known.
    private static async Task<(string Hash12, long Size)> StreamAndHashAsync(string tempPath, Stream source, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = new byte[81920];
        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }
        }
        var hash = hasher.GetHashAndReset();
        return (Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant(), total);
    }

    private static string ShortHashHex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    // Parses the yyyyMMdd-HHmmss timestamp embedded in the canonical base name.
    private static bool TryParseSubmittedAt(string jsonFileName, out DateTimeOffset submittedAt)
    {
        submittedAt = default;
        const string prefix = "problem-report_";
        if (!jsonFileName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = jsonFileName.AsSpan(prefix.Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0) return false;
        var ts = rest[..sep];
        if (!DateTime.TryParseExact(ts, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        submittedAt = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc), TimeSpan.Zero);
        return true;
    }

}
