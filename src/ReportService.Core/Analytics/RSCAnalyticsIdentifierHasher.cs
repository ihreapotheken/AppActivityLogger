using System.Security.Cryptography;
using System.Text;
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

    public RSCAnalyticsIdentifierHasher(RSCAnalyticsOptions options)
    {
        _pepperBytes = Encoding.UTF8.GetBytes(options.IdentifierHashPepper ?? string.Empty);
        _version = options.IdentifierHashVersion;
    }

    public int Version => _version;

    /// <summary>Returns null when <paramref name="raw"/> is null or empty so the storage layer can
    /// distinguish "absent" from "hashed to empty".</summary>
    public string? Hash(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        Span<byte> hash = stackalloc byte[32];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_pepperBytes);
        sha.AppendData(Encoding.UTF8.GetBytes(raw));
        sha.TryGetHashAndReset(hash, out _);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
