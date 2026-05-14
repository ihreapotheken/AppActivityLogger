using Microsoft.Extensions.Hosting;
using ReportService.Security;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies that <see cref="RSCSecretValidation.RequireInProduction"/> refuses to launch when a secret
/// is missing or obviously weak, but tolerates the same inputs in non-Production environments so
/// tests and local development continue to work.
/// </summary>
public class SecretValidationTests
{
    [Fact]
    public void Production_with_empty_secret_throws()
    {
        var env = new FakeEnv("Production");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RSCSecretValidation.RequireInProduction(env, "ReportService:ApiKey", ""));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public void Production_with_short_secret_throws()
    {
        var env = new FakeEnv("Production");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RSCSecretValidation.RequireInProduction(env, "Admin:AdminKey", new string('a', RSCSecretValidation.MinimumSecretLength - 1)));
        Assert.Contains("shorter than", ex.Message);
    }

    [Fact]
    public void Production_with_long_secret_passes()
    {
        var env = new FakeEnv("Production");
        RSCSecretValidation.RequireInProduction(env, "Admin:AdminKey", new string('a', RSCSecretValidation.MinimumSecretLength));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void NonProduction_tolerates_weak_secrets(string envName)
    {
        var env = new FakeEnv(envName);
        RSCSecretValidation.RequireInProduction(env, "ReportService:ApiKey", "");
        RSCSecretValidation.RequireInProduction(env, "Admin:AdminKey", "too-short");
    }

    [Theory]
    // Exact placeholder lines from .env.example (pre-fix and post-fix shapes).
    [InlineData("CHANGEME_GENERATE_WITH_openssl_rand_hex_32")]
    [InlineData("CHANGE_ME_RUN_openssl_rand_hex_32_AND_REPLACE_THIS_VALUE")]
    [InlineData("CHANGE_ME_TO_A_STRONG_SECRET_xxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("replace_me_with_a_real_value_please_and_thank_you_or_else")]
    [InlineData("YOUR_SECRET_HERE_abcdefghijklmnopqrstuvwxyz01234567")]
    [InlineData("this-is-a-PLACEHOLDER-value-for-testing-only-0123456")]
    [InlineData("insecure_dev_only_a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5")]
    public void Production_rejects_placeholder_values_even_when_long_enough(string value)
    {
        Assert.True(value.Length >= RSCSecretValidation.MinimumSecretLength,
            "test input must be long enough to isolate the placeholder-pattern check");

        var env = new FakeEnv("Production");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RSCSecretValidation.RequireInProduction(env, "ReportService:ApiKey", value));
        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a-perfectly-normal-random-looking-hex-0123456789abcdef")]
    public void LooksLikePlaceholder_is_false_for_real_looking_values(string? value)
    {
        Assert.False(RSCSecretValidation.LooksLikePlaceholder(value));
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public FakeEnv(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
