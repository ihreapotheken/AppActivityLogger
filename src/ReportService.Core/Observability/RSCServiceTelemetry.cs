using System.Reflection;

namespace ReportService.Observability;

/// <summary>Telemetry snapshot surfaced by the health endpoints: start time, uptime, assembly informational version. Registered as a singleton.</summary>
public sealed class RSCServiceTelemetry
{
    public DateTimeOffset StartedAt { get; }
    public string Version { get; }
    public long UptimeSeconds => (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds;

    public RSCServiceTelemetry()
    {
        StartedAt = DateTimeOffset.UtcNow;
        Version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
    }
}
