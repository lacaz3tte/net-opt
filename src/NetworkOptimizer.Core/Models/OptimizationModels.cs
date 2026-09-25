namespace NetworkOptimizer.Core;

public sealed class CandidateAttempt
{
    public required int Index { get; init; }
    public required int Total { get; init; }
    public required Candidate Candidate { get; init; }
    public OperationStatus ApplyStatus { get; init; }
    public OperationStatus Result { get; init; }
    public CombinedProbeResult? Probe { get; init; }
    public CandidateScore Score { get; init; } = new();
    public string? Reason { get; init; }
    public TimeSpan Duration { get; init; }
    public bool RolledBack { get; init; }
}

public sealed class OptimizationProgress
{
    public string Phase { get; init; } = "";
    public string Tag { get; init; } = "";
    public int CurrentIndex { get; init; }
    public int Total { get; init; }
    public Candidate? Candidate { get; init; }
    public CombinedProbeResult? LiveProbe { get; init; }
    public OperationStatus Status { get; init; }
    public string Message { get; init; } = "";
}

public sealed class OptimizationResult
{
    public bool Cancelled { get; init; }
    public bool Success { get; init; }
    public Candidate? WinningCandidate { get; init; }
    public CombinedProbeResult? WinningProbe { get; init; }
    public CandidateScore? WinningScore { get; init; }
    public IReadOnlyList<CandidateAttempt> Attempts { get; init; } = Array.Empty<CandidateAttempt>();
    public string? Message { get; init; }
    public DiscoveryReport? Discovery { get; init; }
}

public sealed class WorkingConfiguration
{
    public required string StrategyId { get; init; }
    public required string StrategyName { get; init; }
    public required Candidate Candidate { get; init; }
    public int YouTubeLatencyMs { get; init; }
    public int DiscordLatencyMs { get; init; }
    public int Score { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PendingOperation
{
    public required string StrategyId { get; init; }
    public required string CandidateId { get; init; }
    public required string CandidateName { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public string SnapshotFile { get; init; } = "original-snapshot.json";
}

public sealed class TimeoutSettings
{
    public int ConnectSeconds { get; set; } = 5;
    public int RequestSeconds { get; set; } = 10;
    public int CandidateSeconds { get; set; } = 20;
    public int StabilizationMilliseconds { get; set; } = 800;
    public int HandshakeMilliseconds { get; set; } = 800;
}

public sealed class SearchSettings
{
    public SearchMode Mode { get; set; } = SearchMode.Fast;
    public int MaxCandidates { get; set; } = 32;
    public int MaxStage2PerStrategy { get; set; } = 8;
    public bool AllowDnsChanges { get; set; } = true;
    public bool AllowRoutingChanges { get; set; } = true;
    public int RepeatSuccessProbes { get; set; } = 2;
}

public sealed class MonitorSettings
{
    public int IntervalSeconds { get; set; } = 60;
    public bool AutoRediscover { get; set; } = true;
}

public sealed class ZapretSettings
{
    public int StartupMilliseconds { get; set; } = 1200;
}

public sealed class AppConfiguration
{
    public TimeoutSettings Timeouts { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public MonitorSettings Monitor { get; set; } = new();
    public ZapretSettings Zapret { get; set; } = new();
}
