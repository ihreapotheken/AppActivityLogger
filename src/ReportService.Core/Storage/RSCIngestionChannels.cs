namespace ReportService.Storage;

/// <summary>Stable identifiers for how a report reached the service. Persisted in <c>problem_reports.ingestion_channel</c>.</summary>
public static class RSCIngestionChannels
{
    public const string Multipart = "multipart";
    public const string Json = "json";
}
