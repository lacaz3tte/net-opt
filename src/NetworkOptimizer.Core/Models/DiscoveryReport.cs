namespace NetworkOptimizer.Core;

public sealed class NetworkAdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Type { get; init; }
    public bool Operational { get; init; }
    public bool SupportsIPv4 { get; init; }
    public bool SupportsIPv6 { get; init; }
    public bool IsLoopback { get; init; }
    public bool IsVpnOrTunnel { get; init; }
    public IReadOnlyList<string> UnicastAddresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
    public long? SpeedBps { get; init; }
}

public sealed class RouteEntry
{
    public required string Destination { get; init; }
    public required string MaskOrPrefix { get; init; }
    public required string Gateway { get; init; }
    public required string Interface { get; init; }
    public int Metric { get; init; }
}

public sealed class ProxyEndpoint
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Kind { get; init; }
    public string? Source { get; init; }
    public bool HandshakeOk { get; init; }
    public string? HandshakeReason { get; init; }
}

public sealed class InstalledTool
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Present { get; init; }
    public string? Path { get; init; }
    public string? ProcessName { get; init; }
    public IReadOnlyList<int> ListeningPorts { get; init; } = Array.Empty<int>();
    public string Status => Present ? "AVAILABLE" : "UNAVAILABLE";
}

public sealed class DiscoveryReport
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<NetworkAdapterInfo> Adapters { get; init; } = Array.Empty<NetworkAdapterInfo>();
    public IReadOnlyList<RouteEntry> Routes { get; init; } = Array.Empty<RouteEntry>();
    public ProxySettings InternetSettings { get; init; } = ProxySettings.Disabled;
    public ProxySettings WinHttp { get; init; } = ProxySettings.Disabled;
    public IReadOnlyDictionary<string, string> ProxyEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ProxyEndpoint> LocalProxies { get; init; } = Array.Empty<ProxyEndpoint>();
    public IReadOnlyList<InstalledTool> Tools { get; init; } = Array.Empty<InstalledTool>();
    public bool HasIPv4 { get; init; }
    public bool HasIPv6 { get; init; }
    public bool HasEthernet { get; init; }
    public bool HasWifi { get; init; }
    public bool HasVpnOrTunnel { get; init; }
    public bool IsAdministrator { get; init; }
    public string HostDescription { get; init; } = "";
}
