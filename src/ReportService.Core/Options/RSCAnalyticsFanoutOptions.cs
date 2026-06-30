namespace ReportService.Options;

/// <summary>
/// Tunables for reading across per-app databases. In the database-per-app model an "all clients /
/// all apps" dashboard read (or a worker tick) opens each app's DB and merges in memory; these cap
/// the cost. Bound under the <c>Analytics:Fanout</c> config section.
/// </summary>
public sealed class RSCAnalyticsFanoutOptions
{
    public const string SectionName = "Analytics:Fanout";

    /// <summary>Maximum number of app databases a single all-apps read will touch. Beyond this the
    /// read returns a partial result flagged <c>Truncated</c> (surfaced as a dashboard banner) rather
    /// than opening an unbounded number of files. 0 or negative ⇒ treated as unlimited.</summary>
    public int MaxAppsPerRead { get; init; } = 200;

    /// <summary>Degree of parallelism when opening per-app DBs for a fan-out read. Bounds concurrent
    /// SQLite connections so a wide read can't exhaust file handles. Clamped to at least 1.</summary>
    public int ReadParallelism { get; init; } = 8;

    public int EffectiveParallelism => Math.Max(1, ReadParallelism);

    /// <summary>True when <paramref name="totalApps"/> exceeds the read cap.</summary>
    public bool IsTruncated(int totalApps) => MaxAppsPerRead > 0 && totalApps > MaxAppsPerRead;
}
