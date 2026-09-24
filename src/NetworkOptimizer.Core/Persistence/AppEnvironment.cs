namespace NetworkOptimizer.Core;

public sealed class AppEnvironment
{
    public string Root { get; }
    public string ConfigDir => Path.Combine(Root, "config");
    public string StateDir => Path.Combine(Root, "state");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ConfigFile => Path.Combine(ConfigDir, "appsettings.json");
    public string WorkingFile => Path.Combine(StateDir, "working.json");
    public string PendingFile => Path.Combine(StateDir, "pending-operation.json");
    public string OriginalSnapshotFile => Path.Combine(StateDir, "original-snapshot.json");

    public AppEnvironment(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(LogsDir);
    }

    public static string DefaultRoot()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
            {
                return Path.Combine(local, "NetworkOptimizer");
            }
        }
        catch
        {
            // fall through
        }

        var baseDir = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(baseDir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Path.Combine(baseDir, "data");
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "NetworkOptimizer");
            return fallback;
        }
    }
}
