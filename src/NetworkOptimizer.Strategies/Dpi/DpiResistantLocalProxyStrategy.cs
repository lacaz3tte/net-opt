using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

/// <summary>
/// In-process local proxy for this machine only. Splits the user's own TLS ClientHello
/// across TCP writes. Does not install drivers, capture other devices, or exploit anything.
/// </summary>
public sealed class DpiResistantLocalProxyStrategy : StrategyBase
{
    private LocalSplitProxyServer? _server;
    private NetworkSnapshot? _snapshot;
    private bool _appliedProxy;

    public DpiResistantLocalProxyStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "dpi-local";
    public override string Name => "Local TLS split proxy";
    public override string Description =>
        "Runs a local HTTP CONNECT proxy on 127.0.0.1 and optionally splits TLS ClientHello of this computer's own traffic. Used only when direct paths fail.";

    public override bool IsAvailable()
    {
        if (!Config.Dpi.Enabled)
        {
            AvailabilityReason = "Local TLS split proxy is disabled in configuration";
            return false;
        }

        AvailabilityReason = null;
        return true;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        yield return Make("split-2-ack", "Local TLS split (pos=2, wait ACK)", 40, 1, new CandidateParameters
        {
            SplitMode = "clienthello",
            SplitPosition = 2,
            SplitDelayMs = 30,
            SystemWide = true,
            Extra = new Dictionary<string, string> { ["waitAck"] = "1" }
        });

        yield return Make("split-1-1", "Local TLS split (pos=1, delay=1ms)", 41, 2, new CandidateParameters
        {
            SplitMode = "clienthello",
            SplitPosition = 1,
            SplitDelayMs = 1,
            SystemWide = true
        });

        yield return Make("split-5-5", "Local TLS split (pos=5, delay=5ms)", 42, 2, new CandidateParameters
        {
            SplitMode = "clienthello",
            SplitPosition = 5,
            SplitDelayMs = 5,
            SystemWide = true
        });

        yield return Make("split-10-10", "Local TLS split (pos=10, delay=10ms)", 43, 2, new CandidateParameters
        {
            SplitMode = "clienthello",
            SplitPosition = 10,
            SplitDelayMs = 10,
            SystemWide = true
        });

        yield return Make("plain", "Local proxy without split", 44, 2, new CandidateParameters
        {
            SplitMode = "none",
            SplitPosition = 0,
            SplitDelayMs = 0,
            SystemWide = true
        });
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        await RollbackAsync(cancellationToken);
        try
        {
            _server = new LocalSplitProxyServer(Config.Dpi.ListenAddress, new SplitOptions
            {
                Mode = candidate.Parameters.SplitMode ?? "clienthello",
                Position = candidate.Parameters.SplitPosition ?? 2,
                DelayMs = candidate.Parameters.SplitDelayMs ?? 1,
                WaitForAck = candidate.Parameters.Extra.TryGetValue("waitAck", out var wait) && wait is "1" or "true"
            });
            _server.Start();
            var port = _server.Port;
            _snapshot = await Store.CaptureAsync(cancellationToken);
            await Store.ApplyInternetProxyAsync(new ProxySettings
            {
                Enabled = true,
                Server = $"{Config.Dpi.ListenAddress}:{port}",
                Override = "localhost;127.0.0.1;<local>",
                Source = "network-optimizer"
            }, cancellationToken);
            _appliedProxy = true;
            Log.Info($"apply dpi-local listen={Config.Dpi.ListenAddress}:{port} split={candidate.Parameters.SplitMode}/{candidate.Parameters.SplitPosition}/{candidate.Parameters.SplitDelayMs}");
            return ApplyResult.Ok(new ProbeTransport
            {
                Proxy = new Uri($"http://{Config.Dpi.ListenAddress}:{port}")
            }, _snapshot);
        }
        catch (Exception ex)
        {
            await RollbackAsync(CancellationToken.None);
            return ApplyResult.Fail(ex.Message);
        }
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_appliedProxy && _snapshot is not null)
        {
            try { await Store.RestoreAsync(_snapshot, cancellationToken); }
            catch { /* ignore */ }
        }

        if (_server is not null)
        {
            try { await _server.DisposeAsync(); }
            catch { /* ignore */ }
            _server = null;
        }

        _appliedProxy = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}
