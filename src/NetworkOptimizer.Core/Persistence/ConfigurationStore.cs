using System.Text.Json;

namespace NetworkOptimizer.Core;

public sealed class ConfigurationStore
{
    private readonly AppEnvironment _env;
    private readonly string? _bundledConfigPath;

    public ConfigurationStore(AppEnvironment env, string? bundledConfigPath = null)
    {
        _env = env;
        _bundledConfigPath = bundledConfigPath;
    }

    public AppConfiguration Load()
    {
        EnsureUserCopy();
        if (!File.Exists(_env.ConfigFile))
        {
            return new AppConfiguration();
        }

        try
        {
            var json = File.ReadAllText(_env.ConfigFile);
            return Parse(json);
        }
        catch
        {
            return new AppConfiguration();
        }
    }

    public static AppConfiguration Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppConfiguration();
        }

        var cfg = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions.Default) ?? new AppConfiguration();
        Normalize(cfg);
        return cfg;
    }

    public void Save(AppConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(_env.ConfigDir);
        File.WriteAllText(_env.ConfigFile, JsonSerializer.Serialize(configuration, JsonOptions.Default));
    }

    private void EnsureUserCopy()
    {
        if (File.Exists(_env.ConfigFile))
        {
            return;
        }

        var bundled = _bundledConfigPath
                      ?? Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        if (File.Exists(bundled))
        {
            File.Copy(bundled, _env.ConfigFile, overwrite: false);
        }
        else
        {
            Save(new AppConfiguration());
        }
    }

    internal static void Normalize(AppConfiguration cfg)
    {
        cfg.Timeouts.ConnectSeconds = Clamp(cfg.Timeouts.ConnectSeconds, 1, 30, 5);
        cfg.Timeouts.RequestSeconds = Clamp(cfg.Timeouts.RequestSeconds, 2, 60, 10);
        cfg.Timeouts.CandidateSeconds = Clamp(cfg.Timeouts.CandidateSeconds, 5, 120, 20);
        cfg.Timeouts.StabilizationMilliseconds = Clamp(cfg.Timeouts.StabilizationMilliseconds, 0, 10_000, 800);
        cfg.Timeouts.HandshakeMilliseconds = Clamp(cfg.Timeouts.HandshakeMilliseconds, 100, 5_000, 800);
        cfg.Search.MaxCandidates = Clamp(cfg.Search.MaxCandidates, 1, 500, 32);
        cfg.Search.MaxStage2PerStrategy = Clamp(cfg.Search.MaxStage2PerStrategy, 0, 50, 8);
        cfg.Search.RepeatSuccessProbes = Clamp(cfg.Search.RepeatSuccessProbes, 1, 5, 2);
        cfg.Monitor.IntervalSeconds = Clamp(cfg.Monitor.IntervalSeconds, 5, 3600, 60);
        cfg.Zapret.StartupMilliseconds = Clamp(cfg.Zapret.StartupMilliseconds, 200, 8_000, 1200);
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value < min || value > max) return fallback;
        return value;
    }
}
