namespace NetworkOptimizer.Core;

public sealed class Candidate
{
    public required string Id { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyName { get; init; }
    public required string DisplayName { get; init; }
    public int Stage { get; init; } = 1;
    public int Rank { get; init; }
    public CandidateParameters Parameters { get; init; } = new();
    public string? Notes { get; init; }

    public override string ToString() => $"{StrategyName}: {DisplayName}";
}
