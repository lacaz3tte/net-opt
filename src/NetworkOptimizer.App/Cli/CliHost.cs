using System.IO;
using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.App;

public static class CliHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].Trim().ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        using var services = new AppServices();
        services.Log.Info($"cli {command} {string.Join(' ', rest)}");

        try
        {
            return command switch
            {
                "scan" => await ScanAsync(services),
                "test" => await TestAsync(services),
                "auto" => await AutoAsync(services, rest),
                "status" => await StatusAsync(services),
                "rollback" => await RollbackAsync(services),
                "monitor" => await MonitorAsync(services, rest),
                "help" or "-h" or "--help" => Help(),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped.");
            return 130;
        }
        catch (Exception ex)
        {
            services.Log.Error("cli failed", ex);
            Console.WriteLine("Operation failed.");
            Console.WriteLine("Reason:");
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("Network Optimizer");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  NetworkOptimizer.exe                 Start the graphical interface");
        Console.WriteLine("  NetworkOptimizer.exe auto            Automatic discover & fix");
        Console.WriteLine("  NetworkOptimizer.exe scan            Discover local capabilities");
        Console.WriteLine("  NetworkOptimizer.exe test            Test YouTube and Discord now");
        Console.WriteLine("  NetworkOptimizer.exe status          Show current status");
        Console.WriteLine("  NetworkOptimizer.exe rollback        Restore the original snapshot");
        Console.WriteLine("  NetworkOptimizer.exe monitor [--interval 60] [--auto]");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"Unknown command: {command}");
        Help();
        return 2;
    }

    private static async Task<int> ScanAsync(AppServices services)
    {
        Console.WriteLine("Discovering local network capabilities...");
        var report = await services.Discovery.DiscoverAsync(CancellationToken.None);
        services.Catalog.BindDiscovery(report);

        Console.WriteLine();
        Console.WriteLine($"Host: {report.HostDescription}");
        Console.WriteLine($"Administrator: {(report.IsAdministrator ? "yes" : "no")}");
        Console.WriteLine($"IPv4: {(report.HasIPv4 ? "AVAILABLE" : "UNAVAILABLE")}");
        Console.WriteLine($"IPv6: {(report.HasIPv6 ? "AVAILABLE" : "UNAVAILABLE")}");
        Console.WriteLine($"Ethernet: {(report.HasEthernet ? "AVAILABLE" : "UNAVAILABLE")}");
        Console.WriteLine($"Wi-Fi: {(report.HasWifi ? "AVAILABLE" : "UNAVAILABLE")}");
        Console.WriteLine($"VPN/TUN: {(report.HasVpnOrTunnel ? "AVAILABLE" : "UNAVAILABLE")}");
        Console.WriteLine($"Internet Settings proxy: {report.InternetSettings.Describe()}");
        Console.WriteLine($"WinHTTP proxy: {report.WinHttp.Describe()}");
        Console.WriteLine();
        Console.WriteLine("Adapters:");
        foreach (var a in report.Adapters)
        {
            Console.WriteLine($"  - {a.Name} [{a.Type}] {(a.Operational ? "up" : "down")} {(a.IsVpnOrTunnel ? "VPN/TUN" : "")}");
        }

        Console.WriteLine();
        Console.WriteLine("Local proxy handshakes:");
        if (report.LocalProxies.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var p in report.LocalProxies)
        {
            Console.WriteLine($"  - {p.Kind} {p.Host}:{p.Port}  {(p.HandshakeOk ? "AVAILABLE" : "UNAVAILABLE")}  {p.HandshakeReason}");
        }

        Console.WriteLine();
        Console.WriteLine("Installed tools (never auto-installed):");
        foreach (var tool in report.Tools)
        {
            Console.WriteLine($"  - {tool.Name}: {tool.Status}");
        }

        Console.WriteLine();
        Console.WriteLine("Strategies:");
        foreach (var s in services.Catalog.DescribeAvailability())
        {
            Console.WriteLine($"  - {s.Name}: {s.Status.ToLabel()}" + (s.Reason is null ? "" : $"  ({s.Reason})"));
        }

        return 0;
    }

    private static async Task<int> TestAsync(AppServices services)
    {
        Console.WriteLine("Testing current configuration...");
        var probe = await services.Probe.ProbeAsync(ProbeTransport.SystemDefault, CancellationToken.None);
        PrintProbe(probe);
        return probe.IsFullSuccess ? 0 : 1;
    }

    private static async Task<int> StatusAsync(AppServices services)
    {
        var pending = await services.CrashRecovery.DetectAsync(CancellationToken.None);
        if (pending is not null)
        {
            Console.WriteLine("Detected interrupted network operation.");
            Console.WriteLine($"Strategy: {pending.StrategyId}");
            Console.WriteLine($"Candidate: {pending.CandidateName}");
            Console.WriteLine("Run: NetworkOptimizer.exe rollback");
        }

        var saved = await services.Saved.CheckAsync(CancellationToken.None);
        if (saved.Present)
        {
            Console.WriteLine($"Saved configuration: {saved.Configuration!.StrategyName} / {saved.Configuration.Candidate.DisplayName}");
            Console.WriteLine(saved.Working ? "Saved configuration is WORKING." : "Saved configuration is NOT working.");
        }
        else
        {
            Console.WriteLine("No saved working configuration.");
        }

        var probe = await services.Probe.ProbeAsync(ProbeTransport.SystemDefault, CancellationToken.None);
        Console.WriteLine();
        PrintProbe(probe);
        return 0;
    }

    private static async Task<int> RollbackAsync(AppServices services)
    {
        Console.WriteLine("Restoring original network configuration...");
        var pending = await services.CrashRecovery.DetectAsync(CancellationToken.None);
        if (pending is null && await services.State.LoadOriginalSnapshotAsync(CancellationToken.None) is null)
        {
            Console.WriteLine("No snapshot to restore.");
            return 1;
        }

        await services.Optimizer.RestoreOriginalAsync(CancellationToken.None);
        Console.WriteLine("Original configuration restored.");
        return 0;
    }

    private static async Task<int> AutoAsync(AppServices services, string[] rest)
    {
        if (rest.Contains("--best", StringComparer.OrdinalIgnoreCase))
        {
            services.Configuration.Search.Mode = SearchMode.Best;
        }

        var pending = await services.CrashRecovery.DetectAsync(CancellationToken.None);
        if (pending is not null)
        {
            Console.WriteLine("Previous network configuration was not restored.");
            Console.WriteLine("Restoring original snapshot before search...");
            await services.CrashRecovery.RestoreAsync(CancellationToken.None);
        }

        Console.WriteLine("AUTO DISCOVER & FIX");
        Console.WriteLine();
        var progress = new Progress<OptimizationProgress>(p =>
        {
            if (p.Candidate is not null)
            {
                Console.WriteLine($"[{p.Status.ToLabel()}] {p.CurrentIndex}/{p.Total} {p.Candidate.StrategyName} — {p.Message}");
            }
            else if (!string.IsNullOrWhiteSpace(p.Message))
            {
                Console.WriteLine(p.Message);
            }
        });

        var result = await services.Optimizer.RunAsync(progress, CancellationToken.None);
        Console.WriteLine();
        if (result.Success)
        {
            Console.WriteLine("WORKING CONFIGURATION FOUND");
            Console.WriteLine($"Strategy: {result.WinningCandidate!.StrategyName}");
            Console.WriteLine($"Candidate: {result.WinningCandidate.DisplayName}");
            if (result.WinningProbe is not null) PrintProbe(result.WinningProbe);
            return 0;
        }

        Console.WriteLine(result.Message ?? "No working configuration found.");
        Console.WriteLine($"Candidates tested: {result.Attempts.Count}");
        return 1;
    }

    private static async Task<int> MonitorAsync(AppServices services, string[] rest)
    {
        var interval = services.Configuration.Monitor.IntervalSeconds;
        var auto = services.Configuration.Monitor.AutoRediscover || rest.Contains("--auto", StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] is "--interval" or "-i" && i + 1 < rest.Length && int.TryParse(rest[i + 1], out var sec))
            {
                interval = Math.Clamp(sec, 5, 3600);
            }
        }

        Console.WriteLine($"Monitor started. Interval={interval}s auto={auto}");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var progress = new Progress<OptimizationProgress>(p =>
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {p.Message}");
            if (p.LiveProbe is not null)
            {
                Console.WriteLine($"  YouTube {(p.LiveProbe.YouTubeOk ? "OK" : "FAIL")}  Discord {(p.LiveProbe.DiscordOk ? "OK" : "FAIL")}");
            }
        });

        try
        {
            await services.CreateMonitor().RunAsync(TimeSpan.FromSeconds(interval), auto, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Monitor stopped.");
        }

        return 0;
    }

    private static void PrintProbe(CombinedProbeResult probe)
    {
        PrintService(probe.YouTube);
        Console.WriteLine();
        PrintService(probe.Discord);
        Console.WriteLine();
        Console.WriteLine("Result: " + probe.OverallStatus.ToLabel());
        Console.WriteLine($"Latency: YouTube {probe.YouTube.TotalMs} ms   Discord {probe.Discord.TotalMs} ms");
    }

    private static void PrintService(ServiceProbeResult s)
    {
        Console.WriteLine($"{s.Service}:");
        Console.WriteLine($"    DNS       {(s.Dns.Ok ? "✓" : "✗")}{(s.Dns.Reason is null ? "" : "  " + s.Dns.Reason)}");
        Console.WriteLine($"    TCP       {(s.Tcp.Ok ? "✓" : "✗")}{(s.Tcp.Reason is null ? "" : "  " + s.Tcp.Reason)}");
        Console.WriteLine($"    TLS       {(s.Tls.Ok ? "✓" : "✗")}{(s.Tls.Reason is null ? "" : "  " + s.Tls.Reason)}");
        Console.WriteLine($"    HTTP      {(s.Http.Ok ? "✓" : "✗")}{(s.Http.Reason is null ? "" : "  " + s.Http.Reason)}");
    }
}
