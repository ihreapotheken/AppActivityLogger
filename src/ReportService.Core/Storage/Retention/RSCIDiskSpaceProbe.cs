namespace ReportService.Storage.Retention;

/// <summary>
/// Reports total/free capacity of the filesystem that physically holds a given path. Abstracted so
/// the retention sweep's disk-pressure guard can be unit-tested without a real near-full disk.
/// </summary>
public interface RSCIDiskSpaceProbe
{
    /// <summary>
    /// Total and free bytes of the filesystem containing <paramref name="path"/>, or <c>null</c>
    /// when it can't be determined (path missing, probe error). Callers treat <c>null</c> as
    /// "skip the disk guard this sweep" rather than as pressure.
    /// </summary>
    (long TotalBytes, long FreeBytes)? Probe(string path);
}

/// <summary>
/// Production <see cref="RSCIDiskSpaceProbe"/> backed by <see cref="DriveInfo"/>. Resolves the mount
/// that actually holds the path by longest-prefix match over the mounted drives — important because
/// <c>ReportsRoot</c> (e.g. <c>/srv/reports</c>) is frequently a separate volume from <c>/</c>, so
/// <see cref="Path.GetPathRoot(string)"/> alone would measure the wrong filesystem on Linux.
/// </summary>
public sealed class RSCDriveInfoDiskSpaceProbe : RSCIDiskSpaceProbe
{
    public (long TotalBytes, long FreeBytes)? Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        try
        {
            DriveInfo? best = null;
            var bestLen = -1;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var mount = drive.RootDirectory.FullName;
                // Longest mount point that is a path-prefix of `full` is the filesystem holding it.
                if (full.StartsWith(mount, StringComparison.Ordinal) && mount.Length > bestLen)
                {
                    best = drive;
                    bestLen = mount.Length;
                }
            }

            best ??= new DriveInfo(Path.GetPathRoot(full) ?? "/");
            return (best.TotalSize, best.AvailableFreeSpace);
        }
        catch
        {
            // Unsupported platform, transient IO, or a path on a filesystem DriveInfo can't read.
            return null;
        }
    }
}
