using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// When the primary SQLite tracker can't be constructed (or starts failing), the resilient wrapper
/// must still ban a brute-forcer — policy is fail-closed via the bounded in-memory tracker.
/// </summary>
public class AuthAbuseFallbackTests
{
    [Fact]
    public async Task Resilient_tracker_falls_back_to_in_memory_when_primary_constructor_throws()
    {
        var options = new RSCReportServiceOptions
        {
            AuthAbuseMaxFailures = 3,
            AuthAbuseWindowSeconds = 60,
            AuthAbuseBanSeconds = 60
        };
        var health = new RSCComponentHealth();

        var tracker = new RSCResilientAuthAbuseTracker(
            primaryFactory: () => throw new InvalidOperationException("sqlite unavailable"),
            options: options,
            health: health,
            logger: NullLogger<RSCResilientAuthAbuseTracker>.Instance);

        for (var i = 0; i < options.AuthAbuseMaxFailures; i++)
            await tracker.RecordFailureAsync("1.2.3.4", default);

        var decision = await tracker.CheckAsync("1.2.3.4", default);
        Assert.True(decision.IsBanned, "in-memory fallback must still enforce bans");

        var entry = health.Get(RSCResilientAuthAbuseTracker.Component);
        Assert.NotNull(entry);
        Assert.False(entry!.Healthy);
    }

    [Fact]
    public async Task In_memory_tracker_clears_on_success()
    {
        var options = new RSCReportServiceOptions { AuthAbuseMaxFailures = 3, AuthAbuseWindowSeconds = 60, AuthAbuseBanSeconds = 60 };
        var tracker = new RSCInMemoryAuthAbuseTracker(options);

        await tracker.RecordFailureAsync("9.9.9.9", default);
        await tracker.RecordFailureAsync("9.9.9.9", default);
        await tracker.ClearAsync("9.9.9.9", default);
        Assert.False((await tracker.CheckAsync("9.9.9.9", default)).IsBanned);
    }
}
