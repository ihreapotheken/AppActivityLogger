using System.Security.Cryptography;

namespace ReportService.Security;

/// <summary>
/// Generates managed API keys and hashes them for storage. Keys look like
/// <c>rsk_{id}_{secret}</c> where <c>id</c> is an 8-char public handle (stored verbatim, shown in
/// listings) and <c>secret</c> is 32 cryptographically-random bytes, base64url-encoded.
/// </summary>
/// <remarks>
/// Only <see cref="Hash"/> of the full key is persisted. Because a key is 256 bits of CSPRNG output
/// (not a low-entropy human password), a single unsalted SHA-256 is sufficient and is the accepted
/// approach for API tokens (cf. GitHub PATs) — PBKDF2/bcrypt would add cost without security benefit.
/// </remarks>
public static class RSCApiKeyGenerator
{
    public const string Prefix = "rsk";

    public sealed record Generated(string Id, string PlaintextKey, string Hash);

    /// <summary>Mint a fresh key: a random id, a random secret, the assembled plaintext, and its hash.</summary>
    public static Generated Create()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(); // 8 hex chars
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var plaintext = $"{Prefix}_{id}_{secret}";
        return new Generated(id, plaintext, Hash(plaintext));
    }

    /// <summary>Lowercase hex SHA-256 of the presented key. Used both at creation and at lookup.</summary>
    public static string Hash(string presentedKey)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(presentedKey);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
