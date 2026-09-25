using NetworkOptimizer.Core;

namespace NetworkOptimizer.Strategies;

public abstract class StrategyBase : INetworkStrategy, IDescribedStrategy, IDiscoveryAware
{
    protected StrategyBase(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
    {
        Store = store;
        Config = config;
        Log = log ?? NullLog.Instance;
    }

    protected INetworkConfigurationStore Store { get; }
    protected AppConfiguration Config { get; }
    protected IAppLog Log { get; }

    public DiscoveryReport Discovery { get; set; } = new();

    public void Bind(DiscoveryReport report) => Discovery = report;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    public virtual OperationStatus Availability =>
        IsAvailable() ? OperationStatus.Available : OperationStatus.Unavailable;

    public virtual string? AvailabilityReason { get; protected set; }

    public abstract bool IsAvailable();
    public abstract IEnumerable<Candidate> GenerateCandidates();
    public abstract Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken);
    public abstract Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken);

    protected Candidate Make(string suffix, string display, int rank, int stage, CandidateParameters parameters, string? notes = null) =>
        new()
        {
            Id = $"{Id}:{suffix}",
            StrategyId = Id,
            StrategyName = Name,
            DisplayName = display,
            Rank = rank,
            Stage = stage,
            Parameters = parameters,
            Notes = notes
        };
}

public sealed class StrategyCatalog
{
    public IReadOnlyList<INetworkStrategy> Strategies { get; }
    public IReadOnlyList<StrategyBase> Typed { get; }

    public StrategyCatalog(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null, IZapretRuntime? zapret = null)
    {
        Typed = new StrategyBase[]
        {
            new ZapretStrategy(store, config, zapret ?? new WindowsZapretRuntime(log), log)
        };
        Strategies = Typed;
    }

    public void BindDiscovery(DiscoveryReport report)
    {
        foreach (var strategy in Typed)
        {
            strategy.Discovery = report;
        }
    }

    public IReadOnlyList<StrategyAvailability> DescribeAvailability() =>
        Typed.Select(s => new StrategyAvailability
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            Status = s.Availability,
            Reason = s.AvailabilityReason
        }).ToArray();
}
