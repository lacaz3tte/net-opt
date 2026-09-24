namespace NetworkOptimizer.Core;

public sealed class CandidateScore
{
    public int YouTubeAvailable { get; init; }
    public int DiscordAvailable { get; init; }
    public int StableRepeatedTest { get; init; }
    public int LowLatency { get; init; }
    public int Total => YouTubeAvailable + DiscordAvailable + StableRepeatedTest + LowLatency;

    public static CandidateScore FromProbe(CombinedProbeResult probe, bool stable)
    {
        var youtube = probe.YouTube.HttpOk ? 40 : 0;
        var discord = probe.Discord.HttpOk ? 40 : 0;
        var stablePts = stable && probe.IsFullSuccess ? 10 : 0;
        var latencyPts = 0;
        if (probe.IsFullSuccess)
        {
            var worst = Math.Max(probe.YouTube.TotalMs, probe.Discord.TotalMs);
            if (worst > 0 && worst <= 800) latencyPts = 10;
            else if (worst <= 2000) latencyPts = 6;
            else if (worst <= 4000) latencyPts = 3;
        }

        return new CandidateScore
        {
            YouTubeAvailable = youtube,
            DiscordAvailable = discord,
            StableRepeatedTest = stablePts,
            LowLatency = latencyPts
        };
    }
}
