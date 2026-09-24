using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

public sealed class DnsStrategy : StrategyBase
{
    private static readonly (string Id, string[] Servers)[] PublicResolvers =
    {
        ("cloudflare", new[] { "1.1.1.1", "1.0.0.1" }),
        ("google", new[] { "8.8.8.8", "8.8.4.4" }),
        ("quad9", new[] { "9.9.9.9", "149.112.112.112" })
    };

    private NetworkSnapshot? _snapshot;
    private bool _applied;

    public DnsStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "dns";
    public override string Name => "DNS";
    public override string Description => "Diagnose DNS and, when allowed, switch the active adapter to a well-known public resolver.";

    public override bool IsAvailable()
    {
        if (!Config.Search.AllowDnsChanges)
        {
            AvailabilityReason = "DNS changes are disabled in configuration";
            return false;
        }

        if (!Discovery.Adapters.Any(a => a.Operational && !a.IsLoopback))
        {
            AvailabilityReason = "No active adapter";
            return false;
        }

        if (Store.IsWindows && !Store.IsAdministrator)
        {
            AvailabilityReason = "Administrator permission required to change adapter DNS";
            return false;
        }

        return true;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        var adapter = Discovery.Adapters.FirstOrDefault(a => a.Operational && !a.IsLoopback && a.Gateways.Count > 0)
                      ?? Discovery.Adapters.FirstOrDefault(a => a.Operational && !a.IsLoopback);
        if (adapter is null) yield break;

        yield return Make("current", "Current adapter DNS", 20, 1, new CandidateParameters
        {
            InterfaceId = adapter.Id,
            InterfaceName = adapter.Name,
            DnsServers = adapter.DnsServers.Count > 0 ? adapter.DnsServers : new[] { "system" },
            DnsProvider = "current",
            SystemWide = false
        });

        var rank = 21;
        foreach (var (id, servers) in PublicResolvers)
        {
            yield return Make(id, $"DNS {id} ({servers[0]})", rank++, 1, new CandidateParameters
            {
                InterfaceId = adapter.Id,
                InterfaceName = adapter.Name,
                DnsServers = servers,
                DnsProvider = id,
                SystemWide = true
            });
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var servers = candidate.Parameters.DnsServers ?? Array.Empty<string>();
        var transport = new ProbeTransport
        {
            DnsServers = servers.Count > 0 && servers[0] != "system" ? servers : null
        };

        if (!candidate.Parameters.SystemWide || servers.Count == 0 || servers[0] == "system")
        {
            return ApplyResult.Ok(transport);
        }

        if (Store.IsWindows && !Store.IsAdministrator)
        {
            return ApplyResult.Unavailable("Administrator permission required to change DNS", requiresAdmin: true);
        }

        _snapshot = await Store.CaptureAsync(cancellationToken);
        try
        {
            await Store.ApplyDnsAsync(
                candidate.Parameters.InterfaceId ?? "",
                candidate.Parameters.InterfaceName ?? "",
                servers,
                cancellationToken);
            _applied = true;
            Log.Info($"apply dns provider={candidate.Parameters.DnsProvider} servers={string.Join(",", servers)}");
            return ApplyResult.Ok(transport, _snapshot);
        }
        catch (UnauthorizedAccessException)
        {
            return ApplyResult.Unavailable("Administrator permission required to change DNS", requiresAdmin: true);
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail(ex.Message);
        }
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_applied && _snapshot is not null)
        {
            try { await Store.RestoreAsync(_snapshot, cancellationToken); }
            catch (Exception ex) { return RollbackResult.Fail(ex.Message); }
        }

        _applied = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}
