using System.Diagnostics;
using System.Text;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Strategies;

public sealed class WindowsZapretRuntime : IZapretRuntime
{
    private readonly IAppLog _log;
    private Process? _process;

    public WindowsZapretRuntime(IAppLog? log = null)
    {
        _log = log ?? NullLog.Instance;
    }

    public bool IsRunning => _process is { HasExited: false };

    public bool TryResolve(out ZapretPaths paths, out string? reason)
    {
        paths = default!;
        if (!OperatingSystem.IsWindows())
        {
            reason = "zapret/winws is Windows-only";
            return false;
        }

        if (!IsAdministrator())
        {
            reason = "Run as Administrator (WinDivert needs elevation)";
            return false;
        }

        var root = FindRoot();
        if (root is null)
        {
            reason = "winws.exe not found next to the app (zapret folder)";
            return false;
        }

        var winws = Path.Combine(root, "winws.exe");
        if (!File.Exists(Path.Combine(root, "WinDivert.dll")) ||
            !File.Exists(Path.Combine(root, "WinDivert64.sys")))
        {
            reason = "WinDivert is missing next to winws.exe";
            return false;
        }

        if (!File.Exists(Path.Combine(root, "cygwin1.dll")))
        {
            reason = "cygwin1.dll is missing next to winws.exe";
            return false;
        }

        var hostlist = FirstExisting(
            Path.Combine(root, "files", "youtube-discord.txt"),
            Path.Combine(AppContext.BaseDirectory, "lists", "youtube-discord.txt"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "lists", "youtube-discord.txt"),
            Path.Combine(root, "files", "list-youtube.txt"));
        if (hostlist is null)
        {
            reason = "YouTube/Discord hostlist is missing";
            return false;
        }

        paths = new ZapretPaths(
            root,
            winws,
            hostlist,
            FirstExisting(Path.Combine(root, "files", "quic_initial_www_google_com.bin")),
            FirstExisting(Path.Combine(root, "files", "tls_clienthello_www_google_com.bin")));
        reason = null;
        return true;
    }

    public async Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await StopAsync();
        if (!TryResolve(out var paths, out var reason) || paths is null)
        {
            throw new InvalidOperationException(reason ?? "zapret bundle is not available");
        }

        KillStrayWinws();
        var psi = new ProcessStartInfo
        {
            FileName = paths.WinwsExe,
            WorkingDirectory = paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var output = new StringBuilder();
        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start winws.exe");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _log.Info($"winws start pid={process.Id} args={string.Join(' ', arguments.Take(8))}...");

        try
        {
            await Task.Delay(700, cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }

        if (process.HasExited)
        {
            var text = output.ToString().Trim();
            _process = null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
                ? $"winws exited immediately ({process.ExitCode})"
                : Trim(text));
        }
    }

    public Task StopAsync()
    {
        var process = _process;
        _process = null;
        TryKill(process);
        KillStrayWinws();
        return Task.CompletedTask;
    }

    public static string? FindRoot()
    {
        var bases = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "zapret"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "zapret"),
            Path.Combine(AppContext.BaseDirectory)
        };
        foreach (var dir in bases)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var exe = Path.Combine(dir, "winws.exe");
            if (File.Exists(exe)) return dir;
        }

        return null;
    }

    private static void KillStrayWinws()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("winws"))
            {
                TryKill(p);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // ignore
        }

        try { process.Dispose(); } catch { /* ignore */ }
    }

    private static string? FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string Trim(string text)
    {
        var line = text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? text;
        return line.Length <= 180 ? line : line[..177] + "...";
    }
}
