namespace NetworkOptimizer.Core;

public sealed class SavedConfigurationService
{
    private readonly IStateStore _state;
    private readonly IConnectivityProbe _probe;
    private readonly IAppLog _log;

    public SavedConfigurationService(IStateStore state, IConnectivityProbe probe, IAppLog? log = null)
    {
        _state = state;
        _probe = probe;
        _log = log ?? NullLog.Instance;
    }

    public async Task<WorkingConfigurationCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var working = await _state.LoadWorkingAsync(cancellationToken);
        if (working is null)
        {
            return new WorkingConfigurationCheck { Present = false, Working = false };
        }

        try
        {
            var probe = await _probe.ProbeAsync(ProbeTransport.SystemDefault, cancellationToken);
            var ok = probe.IsFullSuccess;
            _log.Info($"saved configuration check strategy={working.StrategyName} ok={ok}");
            return new WorkingConfigurationCheck
            {
                Present = true,
                Working = ok,
                Configuration = working,
                Probe = probe
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"saved configuration check failed: {ex.Message}");
            return new WorkingConfigurationCheck
            {
                Present = true,
                Working = false,
                Configuration = working
            };
        }
    }
}

public sealed class WorkingConfigurationCheck
{
    public bool Present { get; init; }
    public bool Working { get; init; }
    public WorkingConfiguration? Configuration { get; init; }
    public CombinedProbeResult? Probe { get; init; }
}

public sealed class CrashRecoveryService
{
    private readonly IStateStore _state;
    private readonly INetworkConfigurationStore _store;
    private readonly IAppLog _log;

    public CrashRecoveryService(IStateStore state, INetworkConfigurationStore store, IAppLog? log = null)
    {
        _state = state;
        _store = store;
        _log = log ?? NullLog.Instance;
    }

    public Task<PendingOperation?> DetectAsync(CancellationToken ct) => _state.LoadPendingAsync(ct);

    public async Task RestoreAsync(CancellationToken ct)
    {
        var snapshot = await _state.LoadOriginalSnapshotAsync(ct);
        if (snapshot is null)
        {
            _log.Warn("crash recovery: no original snapshot");
            await _state.ClearPendingAsync(ct);
            return;
        }

        await _store.RestoreAsync(snapshot, ct);
        await _state.ClearPendingAsync(ct);
        _log.Info("crash recovery: original snapshot restored");
    }

    public Task IgnoreAsync(CancellationToken ct) => _state.ClearPendingAsync(ct);
}

public sealed class MonitorService
{
    private readonly IConnectivityProbe _probe;
    private readonly AutoOptimizer _optimizer;
    private readonly AppConfiguration _config;
    private readonly IAppLog _log;

    public MonitorService(IConnectivityProbe probe, AutoOptimizer optimizer, AppConfiguration config, IAppLog? log = null)
    {
        _probe = probe;
        _optimizer = optimizer;
        _config = config;
        _log = log ?? NullLog.Instance;
    }

    public async Task RunAsync(
        TimeSpan interval,
        bool autoRediscover,
        IProgress<OptimizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new OptimizationProgress
            {
                Phase = "monitor",
                Status = OperationStatus.Testing,
                Message = "Checking current configuration..."
            });

            CombinedProbeResult probe;
            try
            {
                probe = await _probe.ProbeAsync(ProbeTransport.SystemDefault, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"monitor probe failed: {ex.Message}");
                probe = new CombinedProbeResult
                {
                    YouTube = new ServiceProbeResult { Service = "YouTube", Http = LayerResult.Fail("Probe failed") },
                    Discord = new ServiceProbeResult { Service = "Discord", Http = LayerResult.Fail("Probe failed") }
                };
            }

            _log.Info($"monitor yt={probe.YouTubeOk} dc={probe.DiscordOk}");
            progress?.Report(new OptimizationProgress
            {
                Phase = "monitor",
                Status = probe.IsFullSuccess ? OperationStatus.Success : OperationStatus.Failed,
                LiveProbe = probe,
                Message = probe.IsFullSuccess
                    ? "YouTube and Discord are reachable."
                    : "Current configuration failed."
            });

            if (!probe.IsFullSuccess && autoRediscover)
            {
                _log.Info("monitor: starting automatic rediscovery");
                progress?.Report(new OptimizationProgress
                {
                    Phase = "rediscover",
                    Status = OperationStatus.Testing,
                    Message = "Starting automatic rediscovery..."
                });
                var result = await _optimizer.RunAsync(progress, cancellationToken);
                if (!result.Success)
                {
                    _log.Warn("monitor: rediscovery did not find a working configuration");
                }
            }

            await Task.Delay(interval, cancellationToken);
        }
    }
}
