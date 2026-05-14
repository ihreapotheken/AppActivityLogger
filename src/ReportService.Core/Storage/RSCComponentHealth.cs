using System.Collections.Concurrent;

namespace ReportService.Storage;

/// <summary>
/// Lightweight health registry for components whose failures we want to surface on the admin
/// dashboard without crashing the request pipeline. Keyed by a stable component name (e.g.
/// <c>"IndexDb"</c>, <c>"AuthAbuseDb"</c>). Thread-safe for concurrent updates from the ingest
/// hot path and the admin UI reader.
/// </summary>
public sealed class RSCComponentHealth
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void MarkHealthy(string component, string? detail = null)
        => _entries[component] = new Entry(true, detail, null, DateTimeOffset.UtcNow);

    public void MarkDegraded(string component, string reason, Exception? error = null)
        => _entries[component] = new Entry(false, reason, error?.GetType().Name, DateTimeOffset.UtcNow);

    public bool IsHealthy(string component)
        => !_entries.TryGetValue(component, out var e) || e.Healthy;

    public Entry? Get(string component)
        => _entries.TryGetValue(component, out var e) ? e : null;

    public IReadOnlyDictionary<string, Entry> Snapshot() =>
        new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);

    public sealed record Entry(bool Healthy, string? Detail, string? ErrorType, DateTimeOffset At);
}
