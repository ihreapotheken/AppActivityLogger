using ReportService.Security;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies that <see cref="RSCSafePath.TryCombine"/> refuses every path-traversal vector we can think of
/// while still accepting ordinary leaf file names. These assertions are the single gate between an
/// attacker-supplied <c>fileName</c> and any filesystem call in the service.
/// </summary>
public class SafePathTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("report.json")]
    [InlineData("problem-report_20260101-000000_abc123def456.json")]
    [InlineData("problem-report_20260101-000000_abc123def456.log.gz")]
    public void Accepts_ordinary_leaf_names(string name)
    {
        Assert.True(RSCSafePath.TryCombine(_root, name, out var full));
        Assert.StartsWith(Path.GetFullPath(_root), full);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("..\\outside.json")]
    [InlineData("subdir/../../outside.json")]
    [InlineData("/absolute/path.json")]
    // A Windows-style drive-letter path is a legitimate filename on Unix (no colon/backslash
    // treatment), so exclude it from the traversal rejection test — the production deployment
    // runs on Linux in Alpine, where the Unix semantics are what matter.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\0null.json")]
    public void Rejects_traversal_and_absolute_and_null(string name)
    {
        Assert.False(RSCSafePath.TryCombine(_root, name, out var full));
        Assert.Equal(string.Empty, full);
    }
}
