namespace NetworkOptimizer.Core;

public sealed class CandidateGenerator
{
    private readonly AppConfiguration _config;
    private readonly IAppLog _log;

    public CandidateGenerator(AppConfiguration config, IAppLog? log = null)
    {
        _config = config;
        _log = log ?? NullLog.Instance;
    }

    public IReadOnlyList<Candidate> Generate(
        IReadOnlyList<INetworkStrategy> strategies,
        DiscoveryReport discovery,
        int stage,
        IReadOnlySet<string>? skipStrategyIds = null)
    {
        var list = new List<Candidate>();
        foreach (var strategy in strategies)
        {
            if (skipStrategyIds is not null && skipStrategyIds.Contains(strategy.Id))
            {
                continue;
            }

            if (!strategy.IsAvailable())
            {
                _log.Info($"generator skip {strategy.Id}: UNAVAILABLE");
                continue;
            }

            IEnumerable<Candidate> produced;
            try
            {
                produced = strategy.GenerateCandidates();
            }
            catch (Exception ex)
            {
                _log.Warn($"generator {strategy.Id} failed: {ex.Message}");
                continue;
            }

            foreach (var candidate in produced)
            {
                if (stage == 1 && candidate.Stage > 1)
                {
                    continue;
                }

                if (stage == 2 && candidate.Stage < 2)
                {
                    continue;
                }

                list.Add(candidate);
            }
        }

        var ranked = list
            .OrderBy(c => c.Stage)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.StrategyName, StringComparer.OrdinalIgnoreCase)
            .Take(_config.Search.MaxCandidates)
            .ToList();

        _log.Info($"generator stage {stage}: {ranked.Count} candidates from {strategies.Count} strategies (adapters={discovery.Adapters.Count}, proxies={discovery.LocalProxies.Count})");
        return ranked;
    }
}
