using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Unit coverage for <see cref="RSCAnalyticsOptions.Validate"/>, the invariant check the ingestion
/// and admin hosts run at startup (fail-fast) so a mis-set numeric/schema option can't silently
/// dead-letter every batch at runtime.
/// </summary>
public class AnalyticsOptionsValidationTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(new RSCAnalyticsOptions().Validate());
    }

    [Fact]
    public void Inverted_schema_range_is_rejected()
    {
        var errors = new RSCAnalyticsOptions { MinAcceptedSchemaVersion = 3, MaxAcceptedSchemaVersion = 1 }.Validate();
        Assert.Contains(errors, e => e.Contains("MinAcceptedSchemaVersion"));
    }

    [Theory]
    [InlineData(nameof(RSCAnalyticsOptions.MaxEventsPerBatch))]
    [InlineData(nameof(RSCAnalyticsOptions.MaxPropertiesPerEvent))]
    [InlineData(nameof(RSCAnalyticsOptions.MaxPropertyValueLength))]
    [InlineData(nameof(RSCAnalyticsOptions.MaxPropertyKeyLength))]
    [InlineData(nameof(RSCAnalyticsOptions.MaxClockSkewSeconds))]
    public void Non_positive_caps_are_rejected(string property)
    {
        var o = property switch
        {
            nameof(RSCAnalyticsOptions.MaxEventsPerBatch)     => new RSCAnalyticsOptions { MaxEventsPerBatch = 0 },
            nameof(RSCAnalyticsOptions.MaxPropertiesPerEvent) => new RSCAnalyticsOptions { MaxPropertiesPerEvent = 0 },
            nameof(RSCAnalyticsOptions.MaxPropertyValueLength)=> new RSCAnalyticsOptions { MaxPropertyValueLength = 0 },
            nameof(RSCAnalyticsOptions.MaxPropertyKeyLength)  => new RSCAnalyticsOptions { MaxPropertyKeyLength = 0 },
            _                                                 => new RSCAnalyticsOptions { MaxClockSkewSeconds = 0 },
        };
        Assert.NotEmpty(o.Validate());
    }
}
