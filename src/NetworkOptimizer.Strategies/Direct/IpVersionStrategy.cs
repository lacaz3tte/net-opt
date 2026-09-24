using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

public sealed class IpVersionStrategy : StrategyBase
{
    private readonly bool _ipv6;
    private NetworkSnapshot? _snapshot;
    private bool _appliedMetric;

    public IpVersionStrategy(INetworkConfigurationStore store, AppConfiguration config, bool ipv6, IAppLog? log = null)
        : base(store, config, log)
    {
        _ipv6 = ipv6;
    }

    public override string Id => _ipv6 ? "ipv6" : "ipv4";
    public override string Name => _ipv6 ? "IPv6" : "IPv4";
    public override string Description => _ipv6
        ? "Prefer IPv6 routes and addresses."
        : "Prefer IPv4 routes and addresses.";

    public override bool IsAvailable()
    {
        var ok = _ipv6 ? Discovery.HasIPv6 : Discovery.HasIPv4;
        AvailabilityReason = ok ? null : (_ipv6 ? "No global IPv6 address on an active adapter" : "No IPv4 address on an active adapter");
        return ok;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        yield return Make("prefer", $"Prefer {Name}", rank: 5, stage: 1, new CandidateParameters
        {
            IpVersion = _ipv6 ? "ipv6" : "ipv4",
            SystemWide = false
        });
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        _snapshot = await Store.CaptureAsync(cancellationToken);
        var family = _ipv6 ? AddressFamilyPreference.IPv6 : AddressFamilyPreference.IPv4;
        return ApplyResult.Ok(new ProbeTransport { AddressFamily = family }, _snapshot);
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_appliedMetric && _snapshot is not null)
        {
            try { await Store.RestoreAsync(_snapshot, cancellationToken); }
            catch { /* ignore */ }
        }

        _appliedMetric = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}
