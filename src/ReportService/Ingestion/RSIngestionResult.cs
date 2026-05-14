using ReportService.Storage;

namespace ReportService.Ingestion;

/// <summary>
/// Outcome of an ingest attempt. Carries the HTTP status the endpoint should map to and, on
/// success, metadata about the persisted report.
/// </summary>
public sealed record RSIngestionResult(bool Success, int HttpStatus, string? Error, RSCStoredReport? Stored)
{
    public static RSIngestionResult Created(RSCStoredReport stored) => new(true, 201, null, stored);
    public static RSIngestionResult BadRequest(string reason) => new(false, 400, reason, null);
    public static RSIngestionResult PayloadTooLarge(string reason) => new(false, 413, reason, null);
    public static RSIngestionResult UnsupportedMediaType(string reason) => new(false, 415, reason, null);
}
