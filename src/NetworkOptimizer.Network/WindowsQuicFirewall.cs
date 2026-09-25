using System.Diagnostics;

namespace NetworkOptimizer.Network;

/// <summary>
/// Optional Windows Firewall rule that blocks outbound QUIC (UDP/443).
/// YouTube often hangs on blocked HTTP/3; forcing TCP lets the local DPI desync path run.
/// The rule is named and always removed on rollback.
/// </summary>
public static class WindowsQuicFirewall
{
    public const string RuleName = "NetworkOptimizer-Block-QUIC-UDP443";

    public static bool TryEnable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        TryDisable();
        var add = Run("netsh",
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block protocol=UDP remoteport=443 enable=yes profile=any");
        return add.ExitCode == 0;
    }

    public static void TryDisable()
    {
        if (!OperatingSystem.IsWindows()) return;
        Run("netsh", $"advfirewall firewall delete rule name=\"{RuleName}\"");
    }

    private static (int ExitCode, string StdErr) Run(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "failed to start");
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "timed out");
            }

            return (p.ExitCode, p.StandardError.ReadToEnd());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
