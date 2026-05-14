namespace ReportService.Hosting;

/// <summary>
/// Configuration for the forwarded-header middleware. Disabled by default: if the service is
/// exposed on a host interface (as the default compose file does), trusting <c>X-Forwarded-For</c>
/// unconditionally lets any caller spoof their source IP and slip past per-IP rate limits + the
/// auth-abuse tracker. Only turn this on when the service sits behind a reverse proxy whose
/// network you can enumerate in <see cref="KnownProxies"/> or <see cref="KnownNetworks"/>.
/// </summary>
public sealed record RSCProxyHeadersOptions
{
    public const string SectionName = "ProxyHeaders";

    /// <summary>When false, <c>X-Forwarded-*</c> headers are ignored entirely.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Exact proxy IPs (v4/v6) whose forwarded headers we trust. Empty + <see cref="Enabled"/>=true means "trust any upstream hop" — safe only if the service is reachable only from a trusted network.</summary>
    public string[] KnownProxies { get; init; } = Array.Empty<string>();

    /// <summary>CIDR ranges (e.g. <c>10.0.0.0/8</c>) whose forwarded headers we trust.</summary>
    public string[] KnownNetworks { get; init; } = Array.Empty<string>();

    /// <summary>How many upstream hops to replay from the forwarded header. Default 1 — the immediate proxy.</summary>
    public int ForwardLimit { get; init; } = 1;
}
