namespace NetworkOptimizer.Core;

/// <summary>
/// User-facing status values. Technical exceptions are mapped to these plus a short reason.
/// </summary>
public enum OperationStatus
{
    Available,
    Unavailable,
    Testing,
    Applying,
    RollingBack,
    Success,
    Partial,
    Failed,
    Timeout
}

public static class OperationStatusDisplay
{
    public static string ToLabel(this OperationStatus status) => status switch
    {
        OperationStatus.Available => "AVAILABLE",
        OperationStatus.Unavailable => "UNAVAILABLE",
        OperationStatus.Testing => "TESTING",
        OperationStatus.Applying => "APPLYING",
        OperationStatus.RollingBack => "ROLLING_BACK",
        OperationStatus.Success => "SUCCESS",
        OperationStatus.Partial => "PARTIAL",
        OperationStatus.Failed => "FAILED",
        OperationStatus.Timeout => "TIMEOUT",
        _ => status.ToString().ToUpperInvariant()
    };
}
