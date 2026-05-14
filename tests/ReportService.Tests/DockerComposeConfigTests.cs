using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Static inspection of <c>docker-compose.yml</c>. Catches regressions where someone removes the
/// required <c>env_file</c>, forgets to pin the state paths for the read-only root filesystem, or
/// re-adds a placeholder secret. Runs without a Docker daemon.
/// </summary>
public class DockerComposeConfigTests
{
    private static readonly string ComposeFile = LocateRepoFile("docker-compose.yml");

    [Fact]
    public void Compose_declares_env_file()
    {
        var content = File.ReadAllText(ComposeFile);
        // The merged stack runs a single service. The env_file directive is parameterised
        // (`${ENV_FILE:-.env}`) so prod and staging pick different files via `scripts/stack.sh`,
        // but the directive itself is mandatory — without it the apiKey + admin key never load.
        var envFileLines = content.Split('\n').Count(l => l.TrimStart().StartsWith("env_file:"));
        Assert.True(envFileLines >= 1, $"expected at least one env_file: directive; found {envFileLines}");
    }

    [Fact]
    public void Compose_pins_all_sqlite_state_paths_under_srv_reports()
    {
        var content = File.ReadAllText(ComposeFile);
        Assert.Contains("ReportService__ReportsRoot=/srv/reports", content);
        Assert.Contains("ReportService__SqliteDbPath=/srv/reports/reports.db", content);
        Assert.Contains("ReportService__AuthAbuseDbPath=/srv/reports/auth-abuse.db", content);
    }

    [Fact]
    public void Compose_keeps_containers_read_only()
    {
        var content = File.ReadAllText(ComposeFile);
        var readOnlyLines = content.Split('\n').Count(l => l.Trim() == "read_only: true");
        Assert.True(readOnlyLines >= 1, $"expected read_only: true on the merged service; found {readOnlyLines}");
    }

    [Fact]
    public void EnvExample_placeholders_would_be_rejected_by_production()
    {
        var envExample = File.ReadAllText(LocateRepoFile(".env.example"));
        foreach (var line in envExample.Split('\n'))
        {
            if (line.StartsWith("ReportService__ApiKey=") || line.StartsWith("Admin__AdminKey="))
            {
                var value = line.Split('=', 2)[1].Trim();
                Assert.True(ReportService.Security.RSCSecretValidation.LooksLikePlaceholder(value),
                    $"placeholder value '{value}' in .env.example would NOT be rejected by RSCSecretValidation");
            }
        }
    }

    private static string LocateRepoFile(string name)
    {
        // Walk up from the test assembly location to the repo root (the first directory that
        // contains ReportService.sln).
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ReportService.sln")))
                return Path.Combine(dir, name);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate repo root containing ReportService.sln");
    }
}
