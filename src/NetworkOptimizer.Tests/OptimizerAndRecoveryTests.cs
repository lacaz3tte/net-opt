using NetworkOptimizer.Core;
using Xunit;

namespace NetworkOptimizer.Tests;

public sealed class OptimizerLoopTests
{
    private static AutoOptimizer Create(
        IReadOnlyList<INetworkStrategy> strategies,
        FakeProbe probe,
        FakeStore store,
        FileStateStore state,
        AppConfiguration cfg)
    {
        var discovery = new StubDiscovery();
        return new AutoOptimizer(strategies, discovery, probe, store, state, cfg, NullLog.Instance);
    }

    [Fact]
    public async Task Successful_candidate_is_kept_and_saved()
    {
        var (env, state, cfg) = TestEnv.Temp();
        cfg.Search.RepeatSuccessProbes = 1;
        cfg.Timeouts.StabilizationMilliseconds = 0;
        var store = new FakeStore();
        var probe = new FakeProbe { Handler = _ => FakeProbe.Fail("down") };
        var strategy = new ScriptedStrategy { Id = "s", Name = "S" };
        strategy.Add("winner");
        var call = 0;
        probe.Handler = _ =>
        {
            call++;
            return call == 1 ? FakeProbe.Fail("down") : FakeProbe.Ok();
        };

        var optimizer = Create(new[] { strategy }, probe, store, state, cfg);
        var result = await optimizer.RunAsync(null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("winner", result.WinningCandidate!.DisplayName);
        Assert.Equal(1, strategy.ApplyCount);
        var working = await state.LoadWorkingAsync(CancellationToken.None);
        Assert.NotNull(working);
        Assert.Null(await state.LoadPendingAsync(CancellationToken.None));
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Failed_candidate_is_rolled_back_then_next_is_tried()
    {
        var (env, state, cfg) = TestEnv.Temp();
        cfg.Search.RepeatSuccessProbes = 1;
        cfg.Timeouts.StabilizationMilliseconds = 0;
        var store = new FakeStore();
        var fail = new ScriptedStrategy { Id = "fail", Name = "Fail" };
        fail.Add("bad");
        var ok = new ScriptedStrategy { Id = "ok", Name = "Ok" };
        ok.Add("good", rank: 2);
        var probe = new FakeProbe
        {
            Handler = t => fail.ApplyCount > 0 && ok.ApplyCount == 0 ? FakeProbe.Fail("nope") :
                ok.ApplyCount > 0 ? FakeProbe.Ok() : FakeProbe.Fail("current")
        };

        var optimizer = Create(new INetworkStrategy[] { fail, ok }, probe, store, state, cfg);
        var result = await optimizer.RunAsync(null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(fail.RollbackCount >= 1);
        Assert.Equal("good", result.WinningCandidate!.DisplayName);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Hung_apply_times_out_and_continues()
    {
        var (env, state, cfg) = TestEnv.Temp();
        cfg.Timeouts.CandidateSeconds = 1;
        cfg.Timeouts.StabilizationMilliseconds = 0;
        cfg.Search.RepeatSuccessProbes = 1;
        var hung = new ScriptedStrategy { Id = "hung", Name = "Hung", ApplyDelay = TimeSpan.FromSeconds(8) };
        hung.Add("slow");
        var ok = new ScriptedStrategy { Id = "ok", Name = "Ok" };
        ok.Add("fast", rank: 2);
        var probe = new FakeProbe { Handler = _ => FakeProbe.Ok() };
        var optimizer = Create(new INetworkStrategy[] { hung, ok }, probe, new FakeStore(), state, cfg);
        var result = await optimizer.RunAsync(null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains(result.Attempts, a => a.Result == OperationStatus.Timeout || a.Candidate.Id.Contains("slow"));
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Stop_restores_original_snapshot()
    {
        var (env, state, cfg) = TestEnv.Temp();
        cfg.Timeouts.StabilizationMilliseconds = 0;
        var strategy = new ScriptedStrategy { Id = "s", Name = "S" };
        strategy.Add("one");
        strategy.Apply = async (_, ct) =>
        {
            await Task.Delay(20_000, ct);
            return ApplyResult.Ok(ProbeTransport.SystemDefault);
        };
        var probe = new FakeProbe { Handler = _ => FakeProbe.Fail("x") };
        var store = new FakeStore();
        var optimizer = Create(new[] { strategy }, probe, store, state, cfg);
        using var cts = new CancellationTokenSource();
        var task = optimizer.RunAsync(null, cts.Token);
        await Task.Delay(150);
        cts.Cancel();
        var result = await task;
        Assert.True(result.Cancelled);
        Assert.True(store.RestoreCount >= 1);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }
}

public sealed class CrashAndSavedTests
{
    [Fact]
    public async Task Crash_recovery_restores_snapshot_and_clears_pending()
    {
        var (env, state, _) = TestEnv.Temp();
        var store = new FakeStore();
        var snapshot = new NetworkSnapshot { Notes = "original", InternetSettings = ProxySettings.Disabled };
        await state.SaveOriginalSnapshotAsync(snapshot, CancellationToken.None);
        await state.SavePendingAsync(new PendingOperation
        {
            StrategyId = "zapret",
            CandidateId = "x",
            CandidateName = "split"
        }, CancellationToken.None);

        var recovery = new CrashRecoveryService(state, store);
        Assert.NotNull(await recovery.DetectAsync(CancellationToken.None));
        await recovery.RestoreAsync(CancellationToken.None);
        Assert.Null(await recovery.DetectAsync(CancellationToken.None));
        Assert.Equal(1, store.RestoreCount);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Saved_configuration_reports_working_when_probe_succeeds()
    {
        var (env, state, _) = TestEnv.Temp();
        await state.SaveWorkingAsync(new WorkingConfiguration
        {
            StrategyId = "zapret",
            StrategyName = "zapret / winws",
            Candidate = new Candidate
            {
                Id = "zapret:fake-multisplit-md5sig-badseq",
                StrategyId = "zapret",
                StrategyName = "zapret / winws",
                DisplayName = "zapret: fake + multisplit midsld + md5sig,badseq"
            }
        }, CancellationToken.None);
        var probe = new FakeProbe { Handler = _ => FakeProbe.Ok() };
        var svc = new SavedConfigurationService(state, probe);
        var check = await svc.CheckAsync(CancellationToken.None);
        Assert.True(check.Present);
        Assert.True(check.Working);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Saved_configuration_reports_failed_when_probe_fails()
    {
        var (env, state, _) = TestEnv.Temp();
        await state.SaveWorkingAsync(new WorkingConfiguration
        {
            StrategyId = "zapret",
            StrategyName = "zapret / winws",
            Candidate = new Candidate
            {
                Id = "zapret:fake-multisplit-md5sig-badseq",
                StrategyId = "zapret",
                StrategyName = "zapret / winws",
                DisplayName = "zapret: fake + multisplit midsld + md5sig,badseq"
            }
        }, CancellationToken.None);
        var probe = new FakeProbe { Handler = _ => FakeProbe.Fail("down") };
        var svc = new SavedConfigurationService(state, probe);
        var check = await svc.CheckAsync(CancellationToken.None);
        Assert.True(check.Present);
        Assert.False(check.Working);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }
}

public sealed class MonitorTests
{
    [Fact]
    public async Task Monitor_triggers_rediscovery_when_current_fails()
    {
        var (env, state, cfg) = TestEnv.Temp();
        cfg.Timeouts.StabilizationMilliseconds = 0;
        cfg.Search.RepeatSuccessProbes = 1;
        var store = new FakeStore();
        var strategy = new ScriptedStrategy { Id = "s", Name = "S" };
        strategy.Add("fix");
        var calls = 0;
        var probe = new FakeProbe
        {
            Handler = _ =>
            {
                calls++;
                // first monitor check fails; optimizer current-probe fails; applied candidate succeeds
                return calls <= 2 ? FakeProbe.Fail("down") : FakeProbe.Ok();
            }
        };
        var optimizer = new AutoOptimizer(new[] { strategy }, new StubDiscovery(), probe, store, state, cfg);
        var monitor = new MonitorService(probe, optimizer, cfg);
        using var cts = new CancellationTokenSource();
        var started = monitor.RunAsync(TimeSpan.FromMilliseconds(50), autoRediscover: true, null, cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && await state.LoadWorkingAsync(CancellationToken.None) is null)
        {
            await Task.Delay(50);
        }

        cts.Cancel();
        try { await started; } catch (OperationCanceledException) { /* expected */ }
        Assert.NotNull(await state.LoadWorkingAsync(CancellationToken.None));
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }
}

internal sealed class StubDiscovery : IDiscoveryService
{
    public Task<DiscoveryReport> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TestEnv.SampleDiscovery());
}
