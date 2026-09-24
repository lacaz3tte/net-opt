namespace NetworkOptimizer.Core;

public sealed class ApplyResult
{
    public bool Applied { get; init; }
    public OperationStatus Status { get; init; }
    public string? Reason { get; init; }
    public ProbeTransport Transport { get; init; } = ProbeTransport.SystemDefault;
    public bool RequiresAdministrator { get; init; }
    public NetworkSnapshot? SnapshotBefore { get; init; }

    public static ApplyResult Ok(ProbeTransport transport, NetworkSnapshot? snapshot = null) => new()
    {
        Applied = true,
        Status = OperationStatus.Success,
        Transport = transport,
        SnapshotBefore = snapshot
    };

    public static ApplyResult Unavailable(string reason, bool requiresAdmin = false) => new()
    {
        Applied = false,
        Status = OperationStatus.Unavailable,
        Reason = reason,
        RequiresAdministrator = requiresAdmin
    };

    public static ApplyResult Fail(string reason) => new()
    {
        Applied = false,
        Status = OperationStatus.Failed,
        Reason = reason
    };

    public static ApplyResult Timeout(string reason) => new()
    {
        Applied = false,
        Status = OperationStatus.Timeout,
        Reason = reason
    };
}

public sealed class RollbackResult
{
    public bool Restored { get; init; }
    public OperationStatus Status { get; init; }
    public string? Reason { get; init; }

    public static RollbackResult Ok() => new() { Restored = true, Status = OperationStatus.Success };

    public static RollbackResult Fail(string reason) => new()
    {
        Restored = false,
        Status = OperationStatus.Failed,
        Reason = reason
    };
}
