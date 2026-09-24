namespace NetworkOptimizer.Core;

public sealed class LayerResult
{
    public bool Ok { get; init; }
    public int LatencyMs { get; init; }
    public string? Reason { get; init; }
    public OperationStatus Status { get; init; }

    public static LayerResult Success(int latencyMs) => new()
    {
        Ok = true,
        LatencyMs = latencyMs,
        Status = OperationStatus.Success
    };

    public static LayerResult Fail(string reason, OperationStatus status = OperationStatus.Failed) => new()
    {
        Ok = false,
        Reason = reason,
        Status = status
    };
}

public sealed class ServiceProbeResult
{
    public required string Service { get; init; }
    public LayerResult Dns { get; init; } = LayerResult.Fail("Not tested", OperationStatus.Unavailable);
    public LayerResult Tcp { get; init; } = LayerResult.Fail("Not tested", OperationStatus.Unavailable);
    public LayerResult Tls { get; init; } = LayerResult.Fail("Not tested", OperationStatus.Unavailable);
    public LayerResult Http { get; init; } = LayerResult.Fail("Not tested", OperationStatus.Unavailable);
    public int TotalMs { get; init; }
    public string? Endpoint { get; init; }

    public bool HttpOk => Http.Ok;
    public bool AnyOk => Dns.Ok || Tcp.Ok || Tls.Ok || Http.Ok;

    public OperationStatus OverallStatus
    {
        get
        {
            if (Http.Ok) return OperationStatus.Success;
            if (Dns.Status == OperationStatus.Timeout || Tcp.Status == OperationStatus.Timeout ||
                Tls.Status == OperationStatus.Timeout || Http.Status == OperationStatus.Timeout)
            {
                return OperationStatus.Timeout;
            }

            if (Tls.Ok || Tcp.Ok) return OperationStatus.Partial;
            return OperationStatus.Failed;
        }
    }
}

public sealed class CombinedProbeResult
{
    public required ServiceProbeResult YouTube { get; init; }
    public required ServiceProbeResult Discord { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool YouTubeOk => YouTube.HttpOk;
    public bool DiscordOk => Discord.HttpOk;
    public bool IsFullSuccess => YouTubeOk && DiscordOk;
    public bool IsPartial => (YouTubeOk ^ DiscordOk) || ((YouTube.AnyOk || Discord.AnyOk) && !IsFullSuccess);
    public bool IsFailed => !YouTubeOk && !DiscordOk;

    public OperationStatus OverallStatus =>
        IsFullSuccess ? OperationStatus.Success :
        IsPartial ? OperationStatus.Partial :
        (YouTube.OverallStatus == OperationStatus.Timeout || Discord.OverallStatus == OperationStatus.Timeout)
            ? OperationStatus.Timeout
            : OperationStatus.Failed;
}

public sealed class ProbeTransport
{
    public static ProbeTransport SystemDefault { get; } = new();

    public Uri? Proxy { get; init; }
    public AddressFamilyPreference AddressFamily { get; init; } = AddressFamilyPreference.Any;
    public IReadOnlyList<string>? DnsServers { get; init; }
    public string? BindAddress { get; init; }
    public bool UseSystemProxy { get; init; }
    public bool BypassProxyOnLocal { get; init; } = true;
    public Action<string>? OnStep { get; init; }

    public ProbeTransport WithStep(Action<string>? onStep) => new()
    {
        Proxy = Proxy,
        AddressFamily = AddressFamily,
        DnsServers = DnsServers,
        BindAddress = BindAddress,
        UseSystemProxy = UseSystemProxy,
        BypassProxyOnLocal = BypassProxyOnLocal,
        OnStep = onStep
    };
}

public enum AddressFamilyPreference
{
    Any,
    IPv4,
    IPv6
}
