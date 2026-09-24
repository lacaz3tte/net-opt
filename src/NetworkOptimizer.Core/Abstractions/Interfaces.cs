namespace NetworkOptimizer.Core;

public interface INetworkStrategy
{
    string Id { get; }
    string Name { get; }

    bool IsAvailable();

    IEnumerable<Candidate> GenerateCandidates();

    Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken);

    Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken);
}

public interface IDescribedStrategy
{
    string Description { get; }
    OperationStatus Availability { get; }
    string? AvailabilityReason { get; }
}

public interface IConnectivityProbe
{
    Task<CombinedProbeResult> ProbeAsync(ProbeTransport transport, CancellationToken cancellationToken);
}

public interface IDiscoveryService
{
    Task<DiscoveryReport> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IDiscoveryAware
{
    void Bind(DiscoveryReport report);
}

public interface INetworkConfigurationStore
{
    bool IsWindows { get; }
    bool IsAdministrator { get; }

    Task<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task RestoreAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken);

    Task ApplyInternetProxyAsync(ProxySettings proxy, CancellationToken cancellationToken);
    Task ApplyDnsAsync(string interfaceId, string interfaceName, IReadOnlyList<string> servers, CancellationToken cancellationToken);
    Task ApplyInterfaceMetricAsync(string interfaceName, int metric, CancellationToken cancellationToken);

    IReadOnlyList<NetworkAdapterInfo> GetAdapters();
    IReadOnlyList<RouteEntry> GetRoutes();
    ProxySettings GetInternetSettingsProxy();
    ProxySettings GetWinHttpProxy();
    IReadOnlyDictionary<string, string> GetProxyEnvironment();
    IReadOnlyList<int> GetLoopbackListeningPorts();
    IReadOnlyList<InstalledTool> DetectInstalledTools();
    Task RefreshProxyNotificationAsync();
}

public interface IAppLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public interface IStateStore
{
    string Root { get; }
    Task SaveOriginalSnapshotAsync(NetworkSnapshot snapshot, CancellationToken ct);
    Task<NetworkSnapshot?> LoadOriginalSnapshotAsync(CancellationToken ct);
    Task SavePendingAsync(PendingOperation pending, CancellationToken ct);
    Task<PendingOperation?> LoadPendingAsync(CancellationToken ct);
    Task ClearPendingAsync(CancellationToken ct);
    Task SaveWorkingAsync(WorkingConfiguration working, CancellationToken ct);
    Task<WorkingConfiguration?> LoadWorkingAsync(CancellationToken ct);
    Task ClearWorkingAsync(CancellationToken ct);
}

public sealed class StrategyAvailability
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required OperationStatus Status { get; init; }
    public string? Reason { get; init; }
}
