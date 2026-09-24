using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NetworkOptimizer.App;

public static class AdminHelper
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRelaunchElevated(string arguments)
    {
        if (!OperatingSystem.IsWindows() || IsAdministrator()) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };
            if (string.IsNullOrWhiteSpace(psi.FileName)) return false;
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class WinConsole
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }
}
