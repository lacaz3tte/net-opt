using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

public sealed class DirectStrategy : StrategyBase
{
    public DirectStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "direct";
    public override string Name => "Direct";
    public override string Description => "Use the current default route with no extra proxy or DNS changes.";

    public override bool IsAvailable()
    {
        var ok = Discovery.Adapters.Any(a => a.Operational && !a.IsLoopback);
        AvailabilityReason = ok ? null : "No active network interface";
        return ok;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        yield return Make("default", "Direct connection", rank: 0, stage: 1, new CandidateParameters
        {
            IpVersion = "any",
            SystemWide = true
        });
    }

    public override Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ApplyResult.Ok(new ProbeTransport
        {
            AddressFamily = AddressFamilyPreference.Any,
            UseSystemProxy = false
        }));
    }

    public override Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RollbackResult.Ok());
}
