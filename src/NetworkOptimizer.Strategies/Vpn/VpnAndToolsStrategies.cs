using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

public sealed class ExistingVpnTunStrategy : StrategyBase
{
    private NetworkSnapshot? _snapshot;
    private bool _applied;

    public ExistingVpnTunStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "vpn-tun";
    public override string Name => "Existing VPN/TUN";
    public override string Description => "If a VPN or TUN/TAP adapter is already present, bind tests to it and optionally prefer its metric.";

    public override bool IsAvailable()
    {
        var ok = Discovery.Adapters.Any(a => a.Operational && a.IsVpnOrTunnel && a.UnicastAddresses.Count > 0);
        AvailabilityReason = ok ? null : "No operational VPN or TUN/TAP adapter";
        return ok;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        var rank = 15;
        foreach (var adapter in Discovery.Adapters.Where(a => a.Operational && a.IsVpnOrTunnel))
        {
            var bind = adapter.UnicastAddresses.FirstOrDefault(a => a.Contains('.'))
                       ?? adapter.UnicastAddresses.FirstOrDefault();
            yield return Make(adapter.Id, $"VPN/TUN {adapter.Name}", rank++, 1, new CandidateParameters
            {
                InterfaceId = adapter.Id,
                InterfaceName = adapter.Name,
                Extra = bind is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["bind"] = bind }
            });
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        candidate.Parameters.Extra.TryGetValue("bind", out var bind);
        var transport = new ProbeTransport { BindAddress = bind };
        if (Config.Search.AllowRoutingChanges && Store.IsAdministrator &&
            !string.IsNullOrWhiteSpace(candidate.Parameters.InterfaceName))
        {
            _snapshot = await Store.CaptureAsync(cancellationToken);
            try
            {
                await Store.ApplyInterfaceMetricAsync(candidate.Parameters.InterfaceName!, 1, cancellationToken);
                _applied = true;
            }
            catch
            {
                _applied = false;
            }
        }

        Log.Info($"apply vpn-tun if={candidate.Parameters.InterfaceName} bind={bind}");
        return ApplyResult.Ok(transport, _snapshot);
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_applied && _snapshot is not null)
        {
            try { await Store.RestoreAsync(_snapshot, cancellationToken); }
            catch { /* ignore */ }
        }

        _applied = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}

public sealed class LocalToolsStrategy : StrategyBase
{
    private NetworkSnapshot? _snapshot;
    private bool _applied;

    public LocalToolsStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "local-tools";
    public override string Name => "Existing local network tools";
    public override string Description => "Detect already installed local tools (Clash, v2ray, sing-box, and similar) and use their existing proxy ports. Missing tools stay UNAVAILABLE and are never installed.";

    public override bool IsAvailable()
    {
        var ok = Discovery.Tools.Any(t => t.Present) &&
                 Discovery.LocalProxies.Any(p => p.HandshakeOk);
        if (!Discovery.Tools.Any(t => t.Present))
        {
            AvailabilityReason = "No known local network tool is running";
            return false;
        }

        if (!Discovery.LocalProxies.Any(p => p.HandshakeOk))
        {
            AvailabilityReason = "A local tool is present but no proxy handshake succeeded";
            return false;
        }

        return true;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        var rank = 11;
        foreach (var tool in Discovery.Tools.Where(t => t.Present))
        {
            var ports = tool.ListeningPorts.Count > 0
                ? tool.ListeningPorts
                : Discovery.LocalProxies.Where(p => p.HandshakeOk).Select(p => p.Port).Distinct().ToArray();
            foreach (var port in ports)
            {
                var endpoint = Discovery.LocalProxies.FirstOrDefault(p => p.Port == port && p.HandshakeOk);
                if (endpoint is null) continue;
                yield return Make($"{tool.Id}:{port}", $"{tool.Name} via {endpoint.Kind} 127.0.0.1:{port}", rank++, 1,
                    new CandidateParameters
                    {
                        ProxyHost = "127.0.0.1",
                        ProxyPort = port,
                        ProxyKind = endpoint.Kind,
                        Extra = new Dictionary<string, string> { ["tool"] = tool.Id }
                    });
            }
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var uri = ProxyUri.TryCreate(candidate.Parameters);
        if (uri is null) return ApplyResult.Unavailable("Local tool endpoint missing");
        _snapshot = await Store.CaptureAsync(cancellationToken);
        var kind = candidate.Parameters.ProxyKind ?? "http";
        var server = kind.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
            ? $"socks={candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}"
            : $"{candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}";
        await Store.ApplyInternetProxyAsync(new ProxySettings
        {
            Enabled = true,
            Server = server,
            Override = "localhost;127.0.0.1;<local>",
            Source = "network-optimizer"
        }, cancellationToken);
        _applied = true;
        Log.Info($"apply local-tool {candidate.DisplayName}");
        return ApplyResult.Ok(new ProbeTransport { Proxy = uri }, _snapshot);
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_applied && _snapshot is not null)
        {
            await Store.RestoreAsync(_snapshot, cancellationToken);
        }

        _applied = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}
