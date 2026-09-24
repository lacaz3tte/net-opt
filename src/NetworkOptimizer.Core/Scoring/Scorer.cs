namespace NetworkOptimizer.Core;

public static class Scorer
{
    public static CandidateScore Score(CombinedProbeResult probe, bool stable) =>
        CandidateScore.FromProbe(probe, stable);

    public static OperationStatus Classify(CombinedProbeResult probe)
    {
        if (probe.IsFullSuccess) return OperationStatus.Success;
        if (probe.YouTubeOk || probe.DiscordOk) return OperationStatus.Partial;
        return probe.OverallStatus;
    }
}
