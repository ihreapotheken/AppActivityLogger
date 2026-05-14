namespace ReportService.Storage;

/// <summary>Outcome of an index rebuild: how many files scanned, indexed before/after, inserted, removed.</summary>
public sealed record RSCRebuildReport(int PlatformsScanned, int FilesOnDisk, int IndexedBefore, int IndexedAfter, int Inserted, int StaleRemoved, TimeSpan Elapsed);
