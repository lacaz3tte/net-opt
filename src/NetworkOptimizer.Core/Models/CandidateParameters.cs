namespace NetworkOptimizer.Core;

/// <summary>
/// Typed knobs the optimizer can iterate without rewriting strategies.
/// </summary>
public sealed class CandidateParameters
{
    public string? ProxyHost { get; init; }
    public int? ProxyPort { get; init; }
    public string? ProxyKind { get; init; }
    public string? IpVersion { get; init; }
    public IReadOnlyList<string>? DnsServers { get; init; }
    public string? DnsProvider { get; init; }
    public string? InterfaceId { get; init; }
    public string? InterfaceName { get; init; }
    public string? SplitMode { get; init; }
    public int? SplitPosition { get; init; }
    public int? SplitDelayMs { get; init; }
    public string? DesyncMode { get; init; }
    public bool BlockQuic { get; init; }
    public bool UseDoh { get; init; }
    public bool SystemWide { get; init; } = true;
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Fingerprint()
    {
        var extra = Extra.Count == 0
            ? ""
            : string.Join(",", Extra.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return string.Join("|",
            ProxyKind ?? "",
            ProxyHost ?? "",
            ProxyPort?.ToString() ?? "",
            IpVersion ?? "",
            DnsProvider ?? "",
            DnsServers is null ? "" : string.Join(",", DnsServers),
            InterfaceId ?? "",
            SplitMode ?? "",
            SplitPosition?.ToString() ?? "",
            SplitDelayMs?.ToString() ?? "",
            DesyncMode ?? "",
            BlockQuic ? "quicblock" : "",
            UseDoh ? "doh" : "",
            SystemWide ? "sys" : "proc",
            extra);
    }
}
