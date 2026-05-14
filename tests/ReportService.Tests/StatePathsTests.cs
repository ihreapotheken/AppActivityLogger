using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Proves the state-path resolver anchors relative SQLite paths under <c>ReportsRoot</c> so the
/// service boots cleanly under a read-only content root. Absolute paths are honored verbatim.
/// </summary>
public class StatePathsTests
{
    [Fact]
    public void Relative_path_is_anchored_under_reports_root()
    {
        var resolved = RSCStatePaths.Resolve("auth-abuse.db", "/srv/reports");
        Assert.Equal(Path.Combine("/srv/reports", "auth-abuse.db"), resolved);
    }

    [Fact]
    public void Absolute_path_is_honored_verbatim()
    {
        var resolved = RSCStatePaths.Resolve("/var/lib/app/state.db", "/srv/reports");
        Assert.Equal("/var/lib/app/state.db", resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_path_returns_empty(string? path)
    {
        Assert.Equal(string.Empty, RSCStatePaths.Resolve(path, "/srv/reports"));
    }
}
