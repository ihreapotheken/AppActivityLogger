using System.Text.Json;
using ReportService.Admin.Options;

namespace ReportService.Admin.Services;

/// <summary>
/// File-system implementation of <see cref="IRSAApiConsoleService"/>. Reads the bundled Postman
/// collection (<c>collection.json</c>) and optional environment (<c>environment.json</c>) from
/// <see cref="RSAAdminOptions.ApiFixturesRoot"/> (resolved against the binary's base directory when
/// relative — the <c>api-fixtures/</c> output folder the csproj populates from repo <c>postman/</c>).
/// </summary>
/// <remarks>
/// The fixtures are static for the process lifetime (they ship in the image and are never edited at
/// runtime), so they are read + validated once and cached in this singleton via a <see cref="Lazy{T}"/>
/// with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>. The collection is parsed only to
/// validate that it is well-formed JSON (a malformed bundle is treated as "no collection" rather than
/// being shipped to the browser to fail there); the raw text is what gets handed to the page.
/// </remarks>
public sealed class RSAApiConsoleService : IRSAApiConsoleService
{
    private const string CollectionFileName = "collection.json";
    private const string EnvironmentFileName = "environment.json";

    private readonly string _root;
    private readonly ILogger<RSAApiConsoleService> _log;
    private readonly Lazy<RSAApiConsoleFixtures?> _fixtures;

    public RSAApiConsoleService(RSAAdminOptions options, ILogger<RSAApiConsoleService> log)
    {
        _log = log;
        var configured = string.IsNullOrWhiteSpace(options.ApiFixturesRoot) ? "api-fixtures" : options.ApiFixturesRoot;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        _fixtures = new Lazy<RSAApiConsoleFixtures?>(LoadUncached, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RSAApiConsoleFixtures? Load() => _fixtures.Value;

    private RSAApiConsoleFixtures? LoadUncached()
    {
        var collectionPath = Path.Combine(_root, CollectionFileName);
        if (!File.Exists(collectionPath))
        {
            _log.LogWarning("API console collection not found at '{Path}'; the page will show an empty state.", collectionPath);
            return null;
        }

        string collectionJson;
        try
        {
            collectionJson = File.ReadAllText(collectionPath);
            using var _ = JsonDocument.Parse(collectionJson); // validate well-formed; throws otherwise
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.LogError(ex, "Failed to read/parse API console collection at '{Path}'.", collectionPath);
            return null;
        }

        // The environment is optional — a collection alone is usable; vars just won't be pre-filled.
        string? environmentJson = null;
        var environmentPath = Path.Combine(_root, EnvironmentFileName);
        if (File.Exists(environmentPath))
        {
            try
            {
                var raw = File.ReadAllText(environmentPath);
                using var _ = JsonDocument.Parse(raw);
                environmentJson = raw;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _log.LogWarning(ex, "API console environment at '{Path}' is unreadable; continuing without it.", environmentPath);
            }
        }

        return new RSAApiConsoleFixtures(collectionJson, environmentJson);
    }
}
