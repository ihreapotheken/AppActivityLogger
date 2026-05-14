namespace ReportService.Admin.ViewModels;

/// <summary>Single canonical byte formatter shared across views (replaces per-page <c>@functions</c> blocks).</summary>
public static class RSAByteFormatter
{
    public static string Format(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024L * 1024) return $"{b / 1024.0:0.#} KiB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024.0:0.##} MiB";
        return $"{b / 1024.0 / 1024.0 / 1024.0:0.##} GiB";
    }

    public static string Format(long? b) => b is null ? "—" : Format(b.Value);
}
