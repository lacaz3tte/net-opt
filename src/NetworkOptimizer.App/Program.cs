using System.IO;
using System.Windows;
using NetworkOptimizer.App.Ui;

namespace NetworkOptimizer.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!AdminHelper.IsAdministrator())
        {
            var quoted = string.Join(" ", args.Select(QuoteArg));
            if (AdminHelper.TryRelaunchElevated(quoted))
            {
                return 0;
            }
        }

        if (args.Length > 0)
        {
            WinConsole.Ensure();
            return CliHost.RunAsync(args).GetAwaiter().GetResult();
        }

        var services = new AppServices();
        services.Log.Info("gui start");
        var vm = new MainViewModel(services);
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            services.Log.Error("unhandled ui exception", e.Exception);
            e.Handled = true;
        };
        var window = new MainWindow(vm);
        window.Closed += (_, _) => services.Dispose();
        app.Run(window);
        return 0;
    }

    private static string QuoteArg(string value) =>
        value.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
