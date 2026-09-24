using System.IO;
using NetworkOptimizer.Core;
using NetworkOptimizer.Network;
using NetworkOptimizer.Probes;
using NetworkOptimizer.Strategies;

namespace NetworkOptimizer.App;

public sealed class AppServices : IDisposable
{
    public AppEnvironment Environment { get; }
    public AppConfiguration Configuration { get; }
    public IAppLog Log { get; }
    public IStateStore State { get; }
    public INetworkConfigurationStore Store { get; }
    public StrategyCatalog Catalog { get; }
    public IDiscoveryService Discovery { get; }
    public IConnectivityProbe Probe { get; }
    public AutoOptimizer Optimizer { get; }
    public CrashRecoveryService CrashRecovery { get; }
    public SavedConfigurationService Saved { get; }

    public AppServices(string? root = null, INetworkConfigurationStore? store = null, IConnectivityProbe? probe = null)
    {
        Environment = new AppEnvironment(root);
        var bundled = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        var configStore = new ConfigurationStore(Environment, bundled);
        Configuration = configStore.Load();
        Log = new FileAppLog(Environment);
        State = new FileStateStore(Environment);
        Store = store ?? NetworkStoreFactory.Create();
        Catalog = new StrategyCatalog(Store, Configuration, Log);
        Discovery = new DiscoveryService(Store, Configuration, Log);
        Probe = probe ?? new ConnectivityProbe(Configuration);
        Optimizer = new AutoOptimizer(Catalog.Strategies, Discovery, Probe, Store, State, Configuration, Log);
        CrashRecovery = new CrashRecoveryService(State, Store, Log);
        Saved = new SavedConfigurationService(State, Probe, Log);
    }

    public MonitorService CreateMonitor() => new(Probe, Optimizer, Configuration, Log);

    public void Dispose()
    {
        foreach (var strategy in Catalog.Strategies)
        {
            try { strategy.RollbackAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* ignore */ }
        }
    }
}
