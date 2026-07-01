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
    /// Identity name of the synthetic principal minted by the loopback auto-sign-in (<see cref="DevAutoSignIn"/>).
    /// Lets the UI tell an auto-signed-in operator — for whom signing out is meaningless, since the next
    /// loopback request re-authenticates and no cookie was ever set — apart from a real <c>/Login</c>
    /// operator (name <c>operator</c>). Shared so Program.cs (which mints the claim) and the layout
    /// (which hides the sign-out button) can't drift.
    /// </summary>
    public const string DevOperatorName = "dev-operator";

    /// <summary>
    /// Space-separated list of origins allowed to embed the admin UI in an <c>&lt;iframe&gt;</c>
    /// (e.g. <c>https://your-site.atlassian.net</c> for Confluence Cloud). Empty (default) keeps the
    /// locked-down posture: <c>X-Frame-Options: DENY</c> + CSP <c>frame-ancestors 'none'</c>, so the UI
    /// cannot be framed by anyone. When set, the admin pages drop <c>X-Frame-Options</c> and emit
    /// <c>frame-ancestors 'self' &lt;origins&gt;</c>, and the auth + scope cookies switch to
    /// <c>SameSite=None; Secure; Partitioned</c> so the cross-site iframe can carry a session.
    /// Requires HTTPS (the cloudflared tunnel terminates TLS), which the <c>Secure</c> flag mandates.
    /// </summary>
    public string EmbedAllowedAncestors { get; init; } = string.Empty;

    /// <summary>True when <see cref="EmbedAllowedAncestors"/> names at least one origin.</summary>
    public bool EmbeddingEnabled => !string.IsNullOrWhiteSpace(EmbedAllowedAncestors);

    /// <summary>
    /// Filesystem folder that the in-app docs preview (<c>/Docs</c>) reads markdown files from.
    /// Relative paths resolve against <see cref="AppContext.BaseDirectory"/>. The default points at
    /// the <c>admin-docs/</c> output folder populated by the csproj's <c>&lt;Content&gt;</c>
    /// includes (repo-root <c>README.md</c> + everything under <c>docs/*.md</c>); operators can
    /// repoint at an external content directory if they prefer to ship docs out-of-band.
    /// </summary>
    public string DocsRoot { get; init; } = "admin-docs";

    /// <summary>
    /// Filesystem folder the in-app API console (<c>/ApiConsole</c>) loads its request collection +
    /// environment from. Relative paths resolve against <see cref="AppContext.BaseDirectory"/>. The
    /// default points at the <c>api-fixtures/</c> output folder populated by the csproj's
    /// <c>&lt;Content&gt;</c> includes (the tracked Postman v2.1 collection + local environment under
    /// repo <c>postman/</c>); operators can repoint at an external collection if they prefer.
    /// </summary>
    public string ApiFixturesRoot { get; init; } = "api-fixtures";
}
