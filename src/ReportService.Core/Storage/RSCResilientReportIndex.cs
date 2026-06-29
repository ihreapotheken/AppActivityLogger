using Microsoft.Extensions.Logging;

namespace ReportService.Storage;

/// <summary>
/// <see cref="RSCIReportIndex"/> decorator that keeps the ingestion + read paths alive when the
/// underlying SQLite index can't be opened or blows up mid-operation. Failures (construction,
/// upsert, list, delete) are caught, logged, and reported via <see cref="RSCComponentHealth"/> under
/// the <see cref="Component"/> key. Ingestion always surfaces a successful 201 as long as the file
/// write itself succeeds; list/search degrade to empty-from-index (the storage decorator unions in
/// disk contents so data is never "lost").
/// </summary>
public sealed class RSCResilientReportIndex : RSCIReportIndex
{
    public const string Component = "IndexDb";

    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<RSCStoredReport> Empty = Array.Empty<RSCStoredReport>();

    private readonly Func<RSCIReportIndex> _factory;
    private readonly RSCComponentHealth _health;
    private readonly ILogger<RSCResilientReportIndex> _logger;

    private readonly object _gate = new();
    private RSCIReportIndex? _inner;
    private DateTimeOffset _nextConstructAttempt = DateTimeOffset.MinValue;

    public RSCResilientReportIndex(Func<RSCIReportIndex> factory, RSCComponentHealth health, ILogger<RSCResilientReportIndex> logger)
    {
        _factory = factory;
        _health = health;
        _logger = logger;
        _ = TryGetInner("startup");
    }

    public Task UpsertAsync(RSCReportMetadata metadata, CancellationToken ct)
        => RunAsync("upsert", inner => inner.UpsertAsync(metadata, ct));

    public Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, CancellationToken ct)
        => RunAsync("list", inner => inner.ListAsync(platform, ct), Empty);

    public Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, int limit, int offset, CancellationToken ct)
        => RunAsync("list", inner => inner.ListAsync(platform, limit, offset, ct), Empty);

    public Task<bool> DeleteAsync(string platform, string fileName, CancellationToken ct)
        => RunAsync("delete", inner => inner.DeleteAsync(platform, fileName, ct), false);

    public Task<bool> RecordLifetimeAndDeleteAsync(string platform, string fileName, CancellationToken ct)
        => RunAsync("delete", inner => inner.RecordLifetimeAndDeleteAsync(platform, fileName, ct), false);

    /// <summary>Exposes the inner index for maintenance paths (rebuild, integrity, backup) that can legitimately surface failure.</summary>
    public RSCIReportIndex? TryGetInnerForMaintenance() => TryGetInner("maintenance");

    private async Task RunAsync(string op, Func<RSCIReportIndex, Task> body)
    {
        var inner = TryGetInner(op);
        if (inner is null) return;
        try
        {
            await body(inner).ConfigureAwait(false);
            _health.MarkHealthy(Component);
        }
        catch (Exception ex)
        {
            ReportFailure(op, ex);
        }
    }

    private async Task<T> RunAsync<T>(string op, Func<RSCIReportIndex, Task<T>> body, T fallback)
    {
        var inner = TryGetInner(op);
        if (inner is null) return fallback;
        try
        {
            var result = await body(inner).ConfigureAwait(false);
            _health.MarkHealthy(Component);
            return result;
        }
        catch (Exception ex)
        {
            ReportFailure(op, ex);
            return fallback;
        }
    }

    private RSCIReportIndex? TryGetInner(string op)
    {
        if (_inner is not null) return _inner;
        lock (_gate)
        {
            if (_inner is not null) return _inner;
            if (DateTimeOffset.UtcNow < _nextConstructAttempt) return null;

            try
            {
                _inner = _factory();
                _health.MarkHealthy(Component, "index constructed");
                return _inner;
            }
            catch (Exception ex)
            {
                _nextConstructAttempt = DateTimeOffset.UtcNow + RetryCooldown;
                _health.MarkDegraded(Component, $"construction failed during {op}: {ex.Message}", ex);
                _logger.LogError(ex, "Report index unavailable (op={Op}); will retry after {Cooldown}", op, RetryCooldown);
                return null;
            }
        }
    }

    private void ReportFailure(string op, Exception ex)
    {
        _health.MarkDegraded(Component, $"{op} failed: {ex.Message}", ex);
        _logger.LogError(ex, "Report index {Op} failed; component marked degraded", op);
    }
}
