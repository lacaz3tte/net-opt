namespace NetworkOptimizer.Core;

public sealed class ProxySettings
{
    public bool Enabled { get; init; }
    public string? Server { get; init; }
    public string? Override { get; init; }
    public bool AutoDetect { get; init; }
    public string? AutoConfigUrl { get; init; }
    public string Source { get; init; } = "none";

    public static ProxySettings Disabled { get; } = new() { Enabled = false, Source = "none" };

    public string Describe()
    {
        if (!Enabled) return "disabled";
        return string.IsNullOrWhiteSpace(Server) ? "enabled (empty server)" : Server!;
    }
}

public sealed class AdapterDnsSettings
{
    public required string InterfaceId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
}

public sealed class NetworkSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public ProxySettings InternetSettings { get; init; } = ProxySettings.Disabled;
    public ProxySettings WinHttp { get; init; } = ProxySettings.Disabled;
    public Dictionary<string, string> ProxyEnvironment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AdapterDnsSettings> Dns { get; init; } = new();
    public Dictionary<string, int> InterfaceMetrics { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> StrategyState { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Notes { get; init; }
}
