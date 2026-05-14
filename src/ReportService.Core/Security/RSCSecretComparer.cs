using System.Security.Cryptography;
using System.Text;

namespace ReportService.Security;

/// <summary>Constant-time UTF-8 shared-secret comparison used by both the API-key handler and the admin login.</summary>
public static class RSCSecretComparer
{
    public static bool Matches(string? supplied, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || supplied is null) return false;
        var s = Encoding.UTF8.GetBytes(supplied);
        var e = Encoding.UTF8.GetBytes(expected);
        return s.Length == e.Length && CryptographicOperations.FixedTimeEquals(s, e);
    }
}
