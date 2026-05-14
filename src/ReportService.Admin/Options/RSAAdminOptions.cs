namespace ReportService.Admin.Options;

/// <summary>Admin UI configuration (section <c>Admin</c>). Distinct from the SDK-facing <c>ReportService:ApiKey</c>.</summary>
public sealed class RSAAdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Operator sign-in secret. Empty disables login. Generate with <c>openssl rand -hex 32</c>; do NOT reuse <c>ReportService:ApiKey</c>.</summary>
    public string AdminKey { get; init; } = string.Empty;

    /// <summary>Cookie lifetime in minutes. Cookie is HttpOnly + SameSite=Strict + (in production) Secure.</summary>
    public int SessionMinutes { get; init; } = 60;

    /// <summary>
    /// Bypass the login screen and sign every request in as a synthetic <c>dev-operator</c>.
    /// Off by default so test hosts (which run with <c>ASPNETCORE_ENVIRONMENT=Development</c> for
    /// the developer-exception page) keep seeing the real auth flow. The local docker-compose
    /// stack sets this to <c>true</c> alongside its <c>127.0.0.1:</c> host-port binding so the
    /// operator never gets prompted on their own machine.
    /// </summary>
    public bool DevAutoSignIn { get; init; } = false;

    /// <summary>
    /// Filesystem folder that the in-app docs preview (<c>/Docs</c>) reads markdown files from.
    /// Relative paths resolve against <see cref="AppContext.BaseDirectory"/>. The default points at
    /// the <c>admin-docs/</c> output folder populated by the csproj's <c>&lt;Content&gt;</c>
    /// includes (repo-root <c>README.md</c> + everything under <c>docs/*.md</c>); operators can
    /// repoint at an external content directory if they prefer to ship docs out-of-band.
    /// </summary>
    public string DocsRoot { get; init; } = "admin-docs";
}
