using ReportService.Storage.Catalog;

namespace ReportService.Tests;

/// <summary>
/// Permissive in-memory <see cref="RSCICatalog"/> for unit tests that don't exercise tenancy
/// validation — every app / client is considered valid, so batches with no explicit attribution
/// (which resolve to the default tenant) pass the validator exactly as they did before the tenancy
/// axes existed. Tests that specifically assert tenancy validation/isolation use the real
/// <see cref="RSCSqliteCatalog"/> instead.
/// </summary>
internal sealed class RSATestCatalog : RSCICatalog
{
    public static readonly RSATestCatalog Permissive = new();

    public bool IsValidClient(string clientSlug) => true;
    public bool IsValidApp(string clientSlug, string appSlug) => true;

    public Task<IReadOnlyList<RSCAppRecord>> ListAppsAsync(string clientSlug, bool includeArchived, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RSCAppRecord>>(Array.Empty<RSCAppRecord>());
    public Task<IReadOnlyList<RSCAppRecord>> ListAllAppsAsync(bool includeArchived, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RSCAppRecord>>(Array.Empty<RSCAppRecord>());
    public Task<RSCAppRecord?> GetAppAsync(string clientSlug, string appSlug, CancellationToken ct) =>
        Task.FromResult<RSCAppRecord?>(null);
    public Task<RSCAppRecord> CreateAppAsync(string clientSlug, string appSlug, string displayName, CancellationToken ct) =>
        Task.FromResult(new RSCAppRecord("app_test", clientSlug, appSlug, displayName, DateTimeOffset.UtcNow, null));
    public Task<bool> RenameAppAsync(string clientSlug, string appSlug, string displayName, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ArchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> UnarchiveAppAsync(string clientSlug, string appSlug, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> DeleteAppAsync(string clientSlug, string appSlug, CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<RSCClientRecord>> ListClientsAsync(bool includeArchived, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RSCClientRecord>>(Array.Empty<RSCClientRecord>());
    public Task<RSCClientRecord?> GetClientAsync(string slug, CancellationToken ct) =>
        Task.FromResult<RSCClientRecord?>(null);
    public Task<RSCClientRecord> CreateClientAsync(string slug, string displayName, CancellationToken ct) =>
        Task.FromResult(new RSCClientRecord("cli_test", slug, displayName, DateTimeOffset.UtcNow, null));
    public Task<bool> RenameClientAsync(string slug, string displayName, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ArchiveClientAsync(string slug, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> UnarchiveClientAsync(string slug, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> DeleteClientAsync(string slug, CancellationToken ct) => Task.FromResult(true);

    public Task<int> CountActiveAppsAsync(CancellationToken ct) => Task.FromResult(0);
    public Task<int> CountActiveClientsAsync(CancellationToken ct) => Task.FromResult(0);
}
