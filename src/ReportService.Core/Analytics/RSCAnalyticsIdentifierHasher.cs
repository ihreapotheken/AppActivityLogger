using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.Analytics;

/// <summary>
/// Pepper-keyed SHA-256 of caller-supplied identifiers (anonymousId, clientId). Every row that
/// reaches the rollup tables carries the hash, never the raw value. The pepper lives in
/// <see cref="RSCAnalyticsOptions.IdentifierHashPepper"/>; rotating it requires bumping
/// <see cref="RSCAnalyticsOptions.IdentifierHashVersion"/> and rebuilding rollups.
/// </summary>
/// <remarks>
/// Why pepper-keyed SHA-256 rather than HMAC: the pepper is process-private (env var, secrets
/// store), so the goal is "don't store raw IDs" and "make rainbow tables expensive", not "prove
/// authenticity". A plain peppered hash is fine for that. If we ever expose a service that needs
/// to verify identifier provenance, this is the place to upgrade.
/// </remarks>
public sealed class RSCAnalyticsIdentifierHasher
{
    private readonly byte[] _pepperBytes;
    private readonly int _version;
    private readonly bool _pepperEmpty;
    private readonly ILogger<RSCAnalyticsIdentifierHasher>? _logger;
    private int _unkeyedWarningEmitted;

    public RSCAnalyticsIdentifierHasher(
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsIdentifierHasher>? logger = null)
    {
        var pepper = options.IdentifierHashPepper ?? string.Empty;
        _pepperBytes = Encoding.UTF8.GetBytes(pepper);
        _pepperEmpty = pepper.Length == 0;
        _version = options.IdentifierHashVersion;
        _logger = logger;
    }

    public int Version => _version;

    /// <summary>Returns null when <paramref name="raw"/> is null or empty so the storage layer can
    /// distinguish "absent" from "hashed to empty".</summary>
    public string? Hash(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        // Runtime guard: an empty pepper degrades the keyed hash to a plain SHA-256 of the raw
        // identifier, which is rainbow-table-reversible for predictable id spaces (account/order
        // ids). The empty-pepper path is kept working for dev/tests, but we warn ONCE so an operator
        // who never set Analytics:IdentifierHashPepper sees that stored identifier hashes are
        // unkeyed. (A startup fail-fast is wired separately in the host's Program.cs.)
        if (_pepperEmpty &&
            _logger is not null &&
            Interlocked.Exchange(ref _unkeyedWarningEmitted, 1) == 0)
        {
            _logger.LogWarning(
                "Analytics identifier hashing is UNKEYED: IdentifierHashPepper is empty, so stored " +
                "anonymousId/clientId hashes are plain SHA-256 and may be reversible for predictable " +
                "identifier spaces. Set Analytics:IdentifierHashPepper to a strong secret.");
        }

        Span<byte> hash = stackalloc byte[32];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_pepperBytes);
        sha.AppendData(Encoding.UTF8.GetBytes(raw));
        sha.TryGetHashAndReset(hash, out _);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
