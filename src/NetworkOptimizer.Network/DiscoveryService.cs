using NetworkOptimizer.Core;

namespace NetworkOptimizer.Network;

public sealed class DiscoveryService : IDiscoveryService
{
    private readonly INetworkConfigurationStore _store;
    private readonly LocalProxyProber _prober;
    private readonly IAppLog _log;
    private readonly int _handshakeMs;

    public DiscoveryService(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
    {
        _store = store;
        _log = log ?? NullLog.Instance;
        _handshakeMs = config.Timeouts.HandshakeMilliseconds;
        _prober = new LocalProxyProber(_handshakeMs);
    }

    public async Task<DiscoveryReport> DiscoverAsync(CancellationToken cancellationToken)
    {
        var adapters = _store.GetAdapters();
        var routes = _store.GetRoutes();
        var internet = _store.GetInternetSettingsProxy();
        var winhttp = _store.GetWinHttpProxy();
        var env = _store.GetProxyEnvironment();
        var tools = _store.DetectInstalledTools();
        var listening = _store.GetLoopbackListeningPorts();

        var candidatePorts = LocalProxyProber.SelectCandidatePorts(listening, tools);
        var endpoints = await _prober.ProbeAsync(candidatePorts, cancellationToken);

        var envEndpoints = ParseEnvironmentProxies(env);
        var merged = Merge(endpoints, envEndpoints);

        var report = new DiscoveryReport
        {
            Adapters = adapters,
            Routes = routes,
            InternetSettings = internet,
            WinHttp = winhttp,
            ProxyEnvironment = env,
            LocalProxies = merged,
            Tools = tools,
            HasIPv4 = adapters.Any(a => a.Operational && a.SupportsIPv4 && !a.IsLoopback && a.UnicastAddresses.Any(IsIPv4)),
            HasIPv6 = adapters.Any(a => a.Operational && a.SupportsIPv6 && !a.IsLoopback && a.UnicastAddresses.Any(IsIPv6)),
            HasEthernet = adapters.Any(a => a.Operational && a.Type == "ethernet"),
            HasWifi = adapters.Any(a => a.Operational && a.Type == "wifi"),
            HasVpnOrTunnel = adapters.Any(a => a.Operational && a.IsVpnOrTunnel),
            IsAdministrator = _store.IsAdministrator,
            HostDescription = $"{Environment.OSVersion} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}"
        };

        _log.Info($"discovery complete adapters={adapters.Count} listening={listening.Count} probed={candidatePorts.Count} handshakeOk={merged.Count(e => e.HandshakeOk)}");
        return report;
    }

    private static List<ProxyEndpoint> ParseEnvironmentProxies(IReadOnlyDictionary<string, string> env)
    {
        var list = new List<ProxyEndpoint>();
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
        {
            if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                var kind = uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) ? "socks5"
                    : uri.Scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase) ? "socks4"
                    : "http";
                list.Add(new ProxyEndpoint
                {
                    Host = uri.Host,
                    Port = uri.Port,
                    Kind = kind,
                    Source = "env:" + key,
                    HandshakeOk = true,
                    HandshakeReason = "environment variable"
                });
            }
        }

        return list;
    }

    private static IReadOnlyList<ProxyEndpoint> Merge(IEnumerable<ProxyEndpoint> a, IEnumerable<ProxyEndpoint> b)
    {
        var map = new Dictionary<string, ProxyEndpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in a.Concat(b))
        {
            var key = $"{e.Kind}:{e.Host}:{e.Port}";
            if (!map.ContainsKey(key) || e.HandshakeOk)
            {
                map[key] = e;
            }
        }

        return map.Values.ToArray();
    }

    private static bool IsIPv4(string address) => System.Net.IPAddress.TryParse(address, out var ip) &&
                                                  ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private static bool IsIPv6(string address) => System.Net.IPAddress.TryParse(address, out var ip) &&
                                                  ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                                                  !ip.IsIPv6LinkLocal;
}
