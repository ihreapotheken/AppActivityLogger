namespace ReportService.Storage;

/// <summary>Snapshot used by the admin status page + dashboard. All fields are best-effort.</summary>
public sealed record RSCIndexStatusReport(
    string DbPath,
    bool Exists,
    long DbSizeBytes,
    bool WalPresent,
    bool ShmPresent,
    int SchemaVersion,
    string? LastIntegrityCheckResult,
    DateTimeOffset? LastIntegrityCheckAt,
    DateTimeOffset? LastBackupAt,
    string? LastBackupPath,
    int DriftMissingInIndex,
    int DriftStaleIndexRows,
    DateTimeOffset? DriftCheckedAt,
    bool Healthy,
    string? HealthDetail,
    DateTimeOffset HealthAt
);
