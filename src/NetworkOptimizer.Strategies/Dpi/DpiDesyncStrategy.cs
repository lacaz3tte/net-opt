using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

/// <summary>
/// Local CONNECT proxy plus zapret/GoodbyeDPI-style HTTPS desync on this PC only:
/// SNI/midsld splits, TLS record fragmentation, OOB, wait-for-ACK, optional QUIC block.
/// </summary>
public sealed class DpiDesyncStrategy : StrategyBase
{
    private LocalSplitProxyServer? _server;
    private NetworkSnapshot? _snapshot;
    private bool _appliedProxy;
    private bool _quicBlocked;

    public DpiDesyncStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "dpi-desync";
    public override string Name => "DPI desync (zapret / GoodbyeDPI)";
    public override string Description =>
        "Local proxy that fragments this computer's TLS ClientHello the way zapret split2/midsld and GoodbyeDPI -e/-s do, and can block QUIC so YouTube falls back to TCP.";

    public override bool IsAvailable()
    {
        if (!Config.Dpi.Enabled)
        {
            AvailabilityReason = "DPI desync is disabled in configuration";
            return false;
        }

        AvailabilityReason = null;
        return true;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        yield return Make("midsld-ack", "DPI desync: split SNI (midsld) + wait ACK + block QUIC", 6, 1,
            Profile(sni: true, at: "midsld", wait: true, delay: 40, quic: true),
            "zapret --dpi-desync=split2 --dpi-desync-split-pos=midsld");

        yield return Make("tlsrec-midsld", "DPI desync: TLS record split at SNI + block QUIC", 7, 1,
            Profile(sni: true, at: "midsld", wait: true, delay: 40, tlsrec: true, quic: true),
            "zapret --dpi-desync=multisplit with TLS record re-framing");

        yield return Make("oob-sni", "DPI desync: OOB + SNI split + wait ACK + block QUIC", 8, 1,
            Profile(sni: true, at: "sni", wait: true, delay: 30, oob: true, quic: true),
            "ByeDPI --oob + --split at SNI");

        yield return Make("goodbyedpi-e2", "DPI desync: GoodbyeDPI -e 2 -s + block QUIC", 9, 1,
            Profile(sni: false, at: "fixed", wait: true, delay: 40, position: 2, quic: true),
            "GoodbyeDPI -e 2 -s");

        yield return Make("multi-midsld", "DPI desync: multisplit 1 + midsld + block QUIC", 10, 2,
            Profile(sni: true, at: "midsld", wait: true, delay: 35, multi: true, position: 1, quic: true),
            "zapret --dpi-desync=fake,multisplit");

        yield return Make("sniext-ack", "DPI desync: split at start of SNI + wait ACK", 11, 2,
            Profile(sni: true, at: "sniext", wait: true, delay: 50, quic: true));

        yield return Make("midsld-noquic", "DPI desync: SNI midsld without QUIC block", 12, 2,
            Profile(sni: true, at: "midsld", wait: true, delay: 40, quic: false));

        yield return Make("e1-s", "DPI desync: GoodbyeDPI -e 1 -s", 13, 2,
            Profile(sni: false, at: "fixed", wait: true, delay: 50, position: 1, quic: true));
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        await RollbackAsync(cancellationToken);
        try
        {
            var p = candidate.Parameters;
            _server = new LocalSplitProxyServer(Config.Dpi.ListenAddress, new SplitOptions
            {
                Mode = p.SplitMode ?? "clienthello",
                Position = p.SplitPosition ?? 2,
                DelayMs = p.SplitDelayMs ?? 40,
                WaitForAck = Flag(p, "waitAck", true),
                SniAware = Flag(p, "sni", false),
                TlsRecordSplit = Flag(p, "tlsrec", false),
                OutOfBand = Flag(p, "oob", false),
                MultiSplit = Flag(p, "multi", false),
                SplitAt = Extra(p, "splitAt")
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

            if (p.BlockQuic && Config.Dpi.BlockQuic && Store.IsWindows)
            {
                _quicBlocked = WindowsQuicFirewall.TryEnable();
                Log.Info(_quicBlocked
                    ? "dpi-desync blocked outbound QUIC (UDP/443)"
                    : "dpi-desync QUIC block skipped (need Administrator / Windows Firewall)");
            }

            Log.Info($"apply dpi-desync listen={Config.Dpi.ListenAddress}:{port} mode={p.DesyncMode} splitAt={Extra(p, "splitAt")} quic={_quicBlocked}");
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
        if (_quicBlocked)
        {
            try { WindowsQuicFirewall.TryDisable(); } catch { /* ignore */ }
            _quicBlocked = false;
        }

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

    private static CandidateParameters Profile(
        bool sni,
        string at,
        bool wait,
        int delay,
        bool quic = true,
        bool tlsrec = false,
        bool oob = false,
        bool multi = false,
        int position = 2)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["splitAt"] = at,
            ["waitAck"] = wait ? "1" : "0",
            ["sni"] = sni ? "1" : "0",
            ["tlsrec"] = tlsrec ? "1" : "0",
            ["oob"] = oob ? "1" : "0",
            ["multi"] = multi ? "1" : "0"
        };
        return new CandidateParameters
        {
            SplitMode = "clienthello",
            SplitPosition = position,
            SplitDelayMs = delay,
            DesyncMode = $"{at}-{(tlsrec ? "tlsrec" : multi ? "multi" : oob ? "oob" : "split")}",
            BlockQuic = quic,
            SystemWide = true,
            Extra = extra
        };
    }

    private static bool Flag(CandidateParameters p, string key, bool fallback)
    {
        if (p.Extra.TryGetValue(key, out var raw))
        {
            return raw is "1" or "true" or "yes";
        }

        return fallback;
    }

    private static string? Extra(CandidateParameters p, string key) =>
        p.Extra.TryGetValue(key, out var raw) ? raw : null;
}
