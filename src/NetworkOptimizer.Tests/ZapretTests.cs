using NetworkOptimizer.Core;
using NetworkOptimizer.Strategies;
using Xunit;

namespace NetworkOptimizer.Tests;

public sealed class ZapretTests
{
    [Fact]
    public void Profiles_include_hostlist_and_youtube_desync_variants()
    {
        var paths = new ZapretPaths("/tmp/zapret", "/tmp/zapret/winws.exe", "/tmp/hosts.txt", "/tmp/quic.bin", "/tmp/tls.bin");
        var profiles = ZapretProfiles.Build(paths);
        Assert.True(profiles.Count >= 8);
        Assert.Equal(profiles.Count, profiles.Select(p => p.Id).Distinct().Count());
        Assert.All(profiles, p => Assert.Equal(1, p.Stage));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.Contains("midsld")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.StartsWith("--dpi-desync=fake,split2")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.Contains("md5sig,badseq")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.StartsWith("--dpi-desync-fake-tls=")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.StartsWith("--wssize=")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.Contains("fakedsplit")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.Contains("syndata")));
        Assert.Contains(profiles, p => p.Id.StartsWith("all443"));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.Contains("quic.bin")));
        Assert.Contains(profiles, p => p.Arguments.Any(a => a.Contains("tls.bin")));
        Assert.Contains("--wf-tcp=80,443", profiles[0].Arguments);
    }

    [Fact]
    public async Task Strategy_starts_runtime_with_profile_args_and_stops_on_rollback()
    {
        var runtime = new FakeZapretRuntime();
        var cfg = new AppConfiguration { Zapret = { StartupMilliseconds = 1 } };
        var strategy = new ZapretStrategy(new FakeStore(), cfg, runtime);
        Assert.True(strategy.IsAvailable());
        var candidate = strategy.GenerateCandidates().First();
        var apply = await strategy.ApplyAsync(candidate, CancellationToken.None);
        Assert.True(apply.Applied);
        Assert.Equal(AddressFamilyPreference.IPv4, apply.Transport.AddressFamily);
        Assert.Equal(1, runtime.StartCount);
        Assert.NotEmpty(runtime.LastArgs);
        Assert.Contains(runtime.LastArgs, a => a.StartsWith("--dpi-desync="));
        var rb = await strategy.RollbackAsync(CancellationToken.None);
        Assert.True(rb.Restored);
        Assert.Equal(1, runtime.StopCount);
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public void Catalog_contains_only_zapret()
    {
        var catalog = new StrategyCatalog(new FakeStore(), new AppConfiguration(), zapret: new FakeZapretRuntime());
        Assert.Single(catalog.Typed);
        Assert.Equal("zapret", catalog.Typed[0].Id);
    }

    [Fact]
    public async Task Optimizer_explains_when_zapret_is_unavailable()
    {
        var (env, state, cfg) = TestEnv.Temp();
        var runtime = new FakeZapretRuntime { Available = false, UnavailableReason = "Run as Administrator (WinDivert needs elevation)" };
        var strategy = new ZapretStrategy(new FakeStore(), cfg, runtime);
        var optimizer = new AutoOptimizer(new INetworkStrategy[] { strategy }, new StubDiscovery(), new FakeProbe(), new FakeStore(), state, cfg);
        var result = await optimizer.RunAsync(null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Administrator", result.Message);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Optimizer_keeps_first_working_zapret_profile()
    {
        var (env, state, cfg) = TestEnv.Temp();
        cfg.Search.RepeatSuccessProbes = 1;
        cfg.Timeouts.StabilizationMilliseconds = 0;
        cfg.Zapret.StartupMilliseconds = 1;
        var runtime = new FakeZapretRuntime();
        var strategy = new ZapretStrategy(new FakeStore(), cfg, runtime);
        var call = 0;
        var probe = new FakeProbe
        {
            Handler = _ =>
            {
                call++;
                return call == 1 ? FakeProbe.Fail("blocked") : FakeProbe.Ok();
            }
        };
        var optimizer = new AutoOptimizer(new INetworkStrategy[] { strategy }, new StubDiscovery(), probe, new FakeStore(), state, cfg);
        var result = await optimizer.RunAsync(null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.StartsWith("zapret:", result.WinningCandidate!.DisplayName);
        Assert.True(runtime.IsRunning);
        try { Directory.Delete(env.Root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Runtime_is_windows_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var runtime = new WindowsZapretRuntime();
        Assert.False(runtime.TryResolve(out _, out var reason));
        Assert.Contains("Windows", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_zapret_startup_milliseconds()
    {
        var cfg = ConfigurationStore.Parse("""{ "zapret": { "startupMilliseconds": 400 } }""");
        Assert.Equal(400, cfg.Zapret.StartupMilliseconds);
    }
}

internal sealed class FakeZapretRuntime : IZapretRuntime
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public IReadOnlyList<string> LastArgs { get; private set; } = Array.Empty<string>();
    public bool IsRunning { get; private set; }
    public bool Available { get; set; } = true;
    public string? UnavailableReason { get; set; }

    public bool TryResolve(out ZapretPaths paths, out string? reason)
    {
        if (!Available)
        {
            paths = default!;
            reason = UnavailableReason ?? "unavailable";
            return false;
        }

        paths = new ZapretPaths("/zapret", "/zapret/winws.exe", "/zapret/hosts.txt", "/zapret/quic.bin", null);
        reason = null;
        return true;
    }

    public Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        LastArgs = arguments.ToArray();
        StartCount++;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }
}
