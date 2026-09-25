using NetworkOptimizer.Core;

namespace NetworkOptimizer.Strategies;

public sealed class ZapretStrategy : StrategyBase
{
    private readonly IZapretRuntime _runtime;

    public ZapretStrategy(
        INetworkConfigurationStore store,
        AppConfiguration config,
        IZapretRuntime runtime,
        IAppLog? log = null)
        : base(store, config, log)
    {
        _runtime = runtime;
    }

    public override string Id => "zapret";
    public override string Name => "zapret / winws";
    public override string Description =>
        "Runs official zapret winws (WinDivert) and tries known YouTube/Discord desync profiles until both sites work.";

    public override bool IsAvailable()
    {
        if (!_runtime.TryResolve(out _, out var reason))
        {
            AvailabilityReason = reason ?? "zapret is unavailable";
            return false;
        }

        AvailabilityReason = null;
        return true;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        if (!_runtime.TryResolve(out var paths, out _))
        {
            yield break;
        }

        foreach (var profile in ZapretProfiles.Build(paths))
        {
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["argv"] = string.Join('\n', profile.Arguments)
            };
            yield return Make(profile.Id, profile.DisplayName, profile.Rank, profile.Stage, new CandidateParameters
            {
                DesyncMode = profile.Id,
                SystemWide = true,
                Extra = extra
            }, profile.Notes);
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        if (!candidate.Parameters.Extra.TryGetValue("argv", out var packed) || string.IsNullOrWhiteSpace(packed))
        {
            return ApplyResult.Unavailable("zapret profile arguments missing");
        }

        var args = packed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            await _runtime.StartAsync(args, cancellationToken);
            var wait = Math.Clamp(Config.Zapret.StartupMilliseconds, 200, 8_000);
            if (wait > 0)
            {
                await Task.Delay(wait, cancellationToken);
            }

            Log.Info($"apply zapret {candidate.DisplayName}");
            return ApplyResult.Ok(ProbeTransport.SystemDefault);
        }
        catch (OperationCanceledException)
        {
            await _runtime.StopAsync();
            throw;
        }
        catch (Exception ex)
        {
            await _runtime.StopAsync();
            return ApplyResult.Fail(ex.Message);
        }
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _runtime.StopAsync();
        return RollbackResult.Ok();
    }
}
