using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Tests;

internal sealed class FakeStore : INetworkConfigurationStore
{
    public NetworkSnapshot Snapshot { get; set; } = new() { Notes = "fake" };
    public ProxySettings Internet { get; set; } = ProxySettings.Disabled;
    public List<NetworkAdapterInfo> Adapters { get; } = new();
    public List<RouteEntry> Routes { get; } = new();
    public List<InstalledTool> Tools { get; } = new();
    public List<int> Listening { get; } = new();
    public int RestoreCount { get; private set; }
    public int ApplyProxyCount { get; private set; }
    public int ApplyDnsCount { get; private set; }
    public bool IsWindows { get; set; }
    public bool IsAdministrator { get; set; } = true;
    public bool ThrowOnRestore { get; set; }

    public Task<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new NetworkSnapshot
        {
            Notes = Snapshot.Notes,
            InternetSettings = Internet,
            Dns = Snapshot.Dns.ToList(),
            ProxyEnvironment = new Dictionary<string, string>(Snapshot.ProxyEnvironment),
            InterfaceMetrics = new Dictionary<string, int>(Snapshot.InterfaceMetrics)
        });

    public Task RestoreAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (ThrowOnRestore) throw new InvalidOperationException("restore failed");
        RestoreCount++;
        Internet = snapshot.InternetSettings;
        Snapshot = snapshot;
        return Task.CompletedTask;
    }

    public Task ApplyInternetProxyAsync(ProxySettings proxy, CancellationToken cancellationToken)
    {
        ApplyProxyCount++;
        Internet = proxy;
        return Task.CompletedTask;
    }

    public Task ApplyDnsAsync(string interfaceId, string interfaceName, IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        ApplyDnsCount++;
        return Task.CompletedTask;
    }

    public Task ApplyInterfaceMetricAsync(string interfaceName, int metric, CancellationToken cancellationToken) => Task.CompletedTask;
    public IReadOnlyList<NetworkAdapterInfo> GetAdapters() => Adapters;
    public IReadOnlyList<RouteEntry> GetRoutes() => Routes;
    public ProxySettings GetInternetSettingsProxy() => Internet;
    public ProxySettings GetWinHttpProxy() => ProxySettings.Disabled;
    public IReadOnlyDictionary<string, string> GetProxyEnvironment() => Snapshot.ProxyEnvironment;
    public IReadOnlyList<int> GetLoopbackListeningPorts() => Listening;
    public IReadOnlyList<InstalledTool> DetectInstalledTools() => Tools;
    public Task RefreshProxyNotificationAsync() => Task.CompletedTask;
}

internal sealed class FakeProbe : IConnectivityProbe
{
    public Func<ProbeTransport, CombinedProbeResult> Handler { get; set; } =
        _ => Fail("not configured");
    public int Calls { get; private set; }
    public TimeSpan Delay { get; set; }

    public async Task<CombinedProbeResult> ProbeAsync(ProbeTransport transport, CancellationToken cancellationToken)
    {
        Calls++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Handler(transport);
    }

    public static CombinedProbeResult Ok(int yt = 80, int dc = 60) => new()
    {
        YouTube = OkService("YouTube", yt),
        Discord = OkService("Discord", dc)
    };

    public static CombinedProbeResult YoutubeOnly() => new()
    {
        YouTube = OkService("YouTube", 90),
        Discord = FailService("Discord", "Connection refused")
    };

    public static CombinedProbeResult Fail(string reason) => new()
    {
        YouTube = FailService("YouTube", reason),
        Discord = FailService("Discord", reason)
    };

    public static ServiceProbeResult OkService(string name, int ms) => new()
    {
        Service = name,
        Dns = LayerResult.Success(5),
        Tcp = LayerResult.Success(10),
        Tls = LayerResult.Success(20),
        Http = LayerResult.Success(ms),
        TotalMs = ms
    };

    public static ServiceProbeResult FailService(string name, string reason) => new()
    {
        Service = name,
        Dns = LayerResult.Fail(reason),
        Http = LayerResult.Fail(reason)
    };
}

internal sealed class ScriptedStrategy : INetworkStrategy, IDiscoveryAware
{
    public string Id { get; init; } = "scripted";
    public string Name { get; init; } = "Scripted";
    public bool Available { get; init; } = true;
    public List<Candidate> Candidates { get; } = new();
    public Func<Candidate, CancellationToken, Task<ApplyResult>>? Apply { get; set; }
    public Func<CancellationToken, Task<RollbackResult>>? Rollback { get; set; }
    public int ApplyCount { get; private set; }
    public int RollbackCount { get; private set; }
    public TimeSpan ApplyDelay { get; set; }
    public DiscoveryReport? Bound { get; private set; }

    public bool IsAvailable() => Available;
    public IEnumerable<Candidate> GenerateCandidates() => Candidates;
    public void Bind(DiscoveryReport report) => Bound = report;

    public async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        ApplyCount++;
        if (ApplyDelay > TimeSpan.Zero)
        {
            await Task.Delay(ApplyDelay, cancellationToken);
        }

        if (Apply is not null) return await Apply(candidate, cancellationToken);
        return ApplyResult.Ok(ProbeTransport.SystemDefault);
    }

    public async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        RollbackCount++;
        if (Rollback is not null) return await Rollback(cancellationToken);
        return RollbackResult.Ok();
    }

    public Candidate Add(string name, int rank = 1, int stage = 1)
    {
        var c = new Candidate
        {
            Id = Id + ":" + name,
            StrategyId = Id,
            StrategyName = Name,
            DisplayName = name,
            Rank = rank,
            Stage = stage
        };
        Candidates.Add(c);
        return c;
    }
}

internal static class TestEnv
{
    public static (AppEnvironment env, FileStateStore state, AppConfiguration cfg) Temp()
    {
        var env = new AppEnvironment(Path.Combine(Path.GetTempPath(), "no-tests-" + Guid.NewGuid().ToString("N")));
        return (env, new FileStateStore(env), new AppConfiguration());
    }

    public static DiscoveryReport SampleDiscovery() => new()
    {
        Adapters = new[]
        {
            new NetworkAdapterInfo
            {
                Id = "eth",
                Name = "Ethernet",
                Description = "Test",
                Type = "ethernet",
                Operational = true,
                SupportsIPv4 = true,
                SupportsIPv6 = true,
                UnicastAddresses = new[] { "10.0.0.2", "fe80::1" },
                Gateways = new[] { "10.0.0.1" },
                DnsServers = new[] { "10.0.0.1" }
            }
        },
        HasIPv4 = true,
        HasIPv6 = false,
        HasEthernet = true
    };
}
