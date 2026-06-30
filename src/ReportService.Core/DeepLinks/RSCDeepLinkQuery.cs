using System.Text;
using System.Text.Json;

namespace ReportService.DeepLinks;

/// <summary>
/// Query-parameter handling for deferred deep linking. A visitor's smart link (or capture call) can
/// carry attribution/campaign parameters (e.g. <c>utm_source</c>, <c>promo</c>); they are
/// <see cref="Normalize"/>d under a configurable cap, stored with the click as a JSON object
/// (<see cref="Serialize"/> / <see cref="Deserialize"/>), and <see cref="Append"/>ed onto the
/// redirect address so the parameters flow through to the app.
/// </summary>
public static class RSCDeepLinkQuery
{
    /// <summary>
    /// Normalises raw query parameters into a bounded, deduplicated map: trims keys, drops empty
    /// keys, keeps the first value seen for a repeated key, clamps each key/value to
    /// <paramref name="maxLen"/> characters, and stops after <paramref name="maxParams"/> entries
    /// (excess dropped, never an error). Insertion order is preserved so the forwarded redirect is
    /// deterministic.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(
        IEnumerable<KeyValuePair<string, string?>>? raw, int maxParams, int maxLen)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw is null) return result;

        maxParams = Math.Max(0, maxParams);
        maxLen = Math.Max(1, maxLen);

        foreach (var kv in raw)
        {
            if (result.Count >= maxParams) break;
            var key = kv.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (key.Length > maxLen) key = key[..maxLen];
            if (result.ContainsKey(key)) continue; // first value wins

            var value = kv.Value ?? string.Empty;
            if (value.Length > maxLen) value = value[..maxLen];

            result[key] = value;
        }
        return result;
    }

    /// <summary>Serialises a parameter map to a compact JSON object, or null when empty/null.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, string>? query) =>
        query is null || query.Count == 0 ? null : JsonSerializer.Serialize(query);

    /// <summary>Deserialises the stored JSON object back into a map; null/blank/garbage → null.</summary>
    public static IReadOnlyDictionary<string, string>? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map is { Count: > 0 } ? map : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends <paramref name="query"/> onto <paramref name="baseUrl"/>'s query string, percent-encoding
    /// each key and value. Preserves an existing query (joins with <c>&amp;</c>) and a trailing
    /// <c>#fragment</c>. Works for both https universal links and custom schemes (e.g.
    /// <c>myapp://promo</c>) because it operates on the string rather than parsing a <see cref="Uri"/>.
    /// Returns <paramref name="baseUrl"/> unchanged when there are no parameters.
    /// </summary>
    public static string Append(string baseUrl, IReadOnlyDictionary<string, string>? query)
    {
        if (string.IsNullOrEmpty(baseUrl) || query is null || query.Count == 0) return baseUrl;

        // Split off a trailing fragment so appended params land before the '#'.
        var hashIdx = baseUrl.IndexOf('#');
        var head = hashIdx >= 0 ? baseUrl[..hashIdx] : baseUrl;
        var fragment = hashIdx >= 0 ? baseUrl[hashIdx..] : string.Empty;

        var sb = new StringBuilder(head);
        var hasQuery = head.IndexOf('?') >= 0;
        foreach (var kv in query)
        {
            sb.Append(hasQuery ? '&' : '?');
            hasQuery = true;
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.Append(fragment).ToString();
    }
}
