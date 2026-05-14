using Microsoft.Extensions.Hosting;

namespace ReportService.Security;

/// <summary>
/// Fail-fast startup check: Production refuses to boot when a shared secret is missing, shorter
/// than <see cref="MinimumSecretLength"/>, or matches one of the obvious placeholder patterns
/// shipped in <c>.env.example</c>. Length alone is not enough — a 64-char
/// <c>CHANGE_ME_TO_A_STRONG_SECRET_xxxxxx</c> passes every entropy check but grants an attacker
/// access to any deployment that forgot to replace it.
/// </summary>
public static class RSCSecretValidation
{
    public const int MinimumSecretLength = 32;

    // Case-insensitive substrings that mark a value as a template, not a real secret. If a real
    // secret ever collides with one of these (astronomically unlikely from openssl rand -hex), the
    // operator can regenerate; false positives cost nothing, false negatives cost everything.
    private static readonly string[] PlaceholderMarkers =
    {
        "CHANGE_ME",
        "CHANGEME",
        "CHANGE-ME",
        "REPLACE_ME",
        "REPLACEME",
        "REPLACE-ME",
        "GENERATE_WITH",
        "PLACEHOLDER",
        "YOUR_SECRET",
        "YOUR-SECRET",
        "EXAMPLE_KEY",
        "INSECURE_DEV_ONLY",
        "SAMPLE_KEY"
    };

    public static void RequireInProduction(IHostEnvironment environment, string name, string? value)
    {
        if (!environment.IsProduction()) return;

        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException(
                $"{name} is not configured. Production refuses to start without a secret.");

        if (value.Length < MinimumSecretLength)
            throw new InvalidOperationException(
                $"{name} is shorter than the minimum of {MinimumSecretLength} characters. " +
                "Generate a strong value, e.g. `openssl rand -hex 32`.");

        if (LooksLikePlaceholder(value))
            throw new InvalidOperationException(
                $"{name} matches a known placeholder pattern — it looks like the value from " +
                ".env.example was copied without being replaced. Generate a real secret, e.g. " +
                "`openssl rand -hex 32`, and set it before starting the service.");
    }

    /// <summary>
    /// True when <paramref name="value"/> contains any of <see cref="PlaceholderMarkers"/>
    /// (case-insensitive). Exposed for the test suite.
    /// </summary>
    public static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var marker in PlaceholderMarkers)
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
