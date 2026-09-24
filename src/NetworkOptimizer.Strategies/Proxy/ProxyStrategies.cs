using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Strategies;

public sealed class ExistingHttpProxyStrategy : StrategyBase
{
    private NetworkSnapshot? _snapshot;
    private bool _applied;

    public ExistingHttpProxyStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "http-proxy";
    public override string Name => "Existing HTTP/HTTPS Proxy";
    public override string Description => "Use an HTTP proxy that is already running on this computer or configured in the environment.";

    public override bool IsAvailable()
    {
        var ok = Discovery.LocalProxies.Any(p => p.HandshakeOk && p.Kind is "http" or "https");
        AvailabilityReason = ok ? null : "No HTTP proxy endpoint passed handshake";
        return ok;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        var rank = 10;
        foreach (var proxy in Discovery.LocalProxies.Where(p => p.HandshakeOk && p.Kind is "http" or "https"))
        {
            yield return Make($"{proxy.Host}:{proxy.Port}", $"HTTP proxy {proxy.Host}:{proxy.Port}", rank++, 1,
                new CandidateParameters
                {
                    ProxyHost = proxy.Host,
                    ProxyPort = proxy.Port,
                    ProxyKind = "http",
                    SystemWide = true
                }, proxy.Source);
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var uri = ProxyUri.TryCreate(candidate.Parameters);
        if (uri is null)
        {
            return ApplyResult.Unavailable("Proxy endpoint missing");
        }

        _snapshot = await Store.CaptureAsync(cancellationToken);
        await Store.ApplyInternetProxyAsync(new ProxySettings
        {
            Enabled = true,
            Server = $"{candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}",
            Override = "localhost;127.0.0.1;<local>",
            Source = "network-optimizer"
        }, cancellationToken);
        _applied = true;
        Log.Info($"apply http-proxy {candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}");
        return ApplyResult.Ok(new ProbeTransport { Proxy = uri }, _snapshot);
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_applied && _snapshot is not null)
        {
            await Store.RestoreAsync(_snapshot, cancellationToken);
        }

        _applied = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}

public sealed class ExistingSocksStrategy : StrategyBase
{
    private NetworkSnapshot? _snapshot;
    private bool _applied;

    public ExistingSocksStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "socks-proxy";
    public override string Name => "Existing SOCKS4/SOCKS5";
    public override string Description => "Use a SOCKS proxy that is already running locally and passed a handshake probe.";

    public override bool IsAvailable()
    {
        var ok = Discovery.LocalProxies.Any(p => p.HandshakeOk && p.Kind.StartsWith("socks", StringComparison.OrdinalIgnoreCase));
        AvailabilityReason = ok ? null : "No SOCKS endpoint passed handshake";
        return ok;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        var rank = 12;
        foreach (var proxy in Discovery.LocalProxies.Where(p => p.HandshakeOk && p.Kind.StartsWith("socks", StringComparison.OrdinalIgnoreCase)))
        {
            yield return Make($"{proxy.Kind}:{proxy.Host}:{proxy.Port}",
                $"{proxy.Kind.ToUpperInvariant()} {proxy.Host}:{proxy.Port}", rank++, 1,
                new CandidateParameters
                {
                    ProxyHost = proxy.Host,
                    ProxyPort = proxy.Port,
                    ProxyKind = proxy.Kind,
                    SystemWide = true
                }, proxy.Source);
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var uri = ProxyUri.TryCreate(candidate.Parameters);
        if (uri is null) return ApplyResult.Unavailable("SOCKS endpoint missing");

        _snapshot = await Store.CaptureAsync(cancellationToken);
        var kind = candidate.Parameters.ProxyKind ?? "socks5";
        var server = kind.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
            ? $"socks={candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}"
            : $"{candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}";
        await Store.ApplyInternetProxyAsync(new ProxySettings
        {
            Enabled = true,
            Server = server,
            Override = "localhost;127.0.0.1;<local>",
            Source = "network-optimizer"
        }, cancellationToken);
        _applied = true;
        Log.Info($"apply socks {kind} {candidate.Parameters.ProxyHost}:{candidate.Parameters.ProxyPort}");
        return ApplyResult.Ok(new ProbeTransport { Proxy = uri }, _snapshot);
    }

    public override async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        if (_applied && _snapshot is not null)
        {
            await Store.RestoreAsync(_snapshot, cancellationToken);
        }

        _applied = false;
        _snapshot = null;
        return RollbackResult.Ok();
    }
}

public sealed class WindowsProxyStrategy : StrategyBase
{
    private NetworkSnapshot? _snapshot;

    public WindowsProxyStrategy(INetworkConfigurationStore store, AppConfiguration config, IAppLog? log = null)
        : base(store, config, log) { }

    public override string Id => "windows-proxy";
    public override string Name => "Windows Proxy";
    public override string Description => "Test the current WinINET / WinHTTP / environment proxy configuration without installing anything.";

    public override bool IsAvailable()
    {
        var ok = Discovery.InternetSettings.Enabled
                 || Discovery.WinHttp.Enabled
                 || Discovery.ProxyEnvironment.Keys.Any(k => k is "HTTP_PROXY" or "HTTPS_PROXY" or "ALL_PROXY");
        AvailabilityReason = ok ? null : "No Windows or environment proxy is configured";
        return ok;
    }

    public override IEnumerable<Candidate> GenerateCandidates()
    {
        if (Discovery.InternetSettings.Enabled)
        {
            yield return Make("wininet", "Current Internet Settings proxy", 8, 1, new CandidateParameters
            {
                Extra = new Dictionary<string, string> { ["source"] = "internet-settings" }
            }, Discovery.InternetSettings.Describe());
        }

        if (Discovery.WinHttp.Enabled)
        {
            yield return Make("winhttp", "Current WinHTTP proxy", 9, 1, new CandidateParameters
            {
                Extra = new Dictionary<string, string> { ["source"] = "winhttp" }
            }, Discovery.WinHttp.Describe());
        }

        if (Discovery.ProxyEnvironment.TryGetValue("HTTP_PROXY", out var http) ||
            Discovery.ProxyEnvironment.TryGetValue("HTTPS_PROXY", out http) ||
            Discovery.ProxyEnvironment.TryGetValue("ALL_PROXY", out http))
        {
            yield return Make("env", "Environment proxy variables", 9, 1, new CandidateParameters
            {
                Extra = new Dictionary<string, string> { ["source"] = "env", ["value"] = http }
            });
        }
    }

    public override async Task<ApplyResult> ApplyAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        _snapshot = await Store.CaptureAsync(cancellationToken);
        Uri? proxy = null;
        var source = candidate.Parameters.Extra.TryGetValue("source", out var s) ? s : "internet-settings";
        if (source == "env" && candidate.Parameters.Extra.TryGetValue("value", out var raw) &&
            Uri.TryCreate(raw, UriKind.Absolute, out var envUri))
        {
            proxy = envUri;
        }
        else if (Discovery.InternetSettings.Enabled && !string.IsNullOrWhiteSpace(Discovery.InternetSettings.Server))
        {
            var server = Discovery.InternetSettings.Server!;
            if (!server.Contains("://", StringComparison.Ordinal))
            {
                server = "http://" + server.Replace("socks=", "", StringComparison.OrdinalIgnoreCase);
            }

            Uri.TryCreate(server, UriKind.Absolute, out proxy);
        }
        else if (Discovery.WinHttp.Enabled && !string.IsNullOrWhiteSpace(Discovery.WinHttp.Server))
        {
            var server = Discovery.WinHttp.Server!;
            if (!server.Contains("://", StringComparison.Ordinal)) server = "http://" + server;
            Uri.TryCreate(server, UriKind.Absolute, out proxy);
        }

        return ApplyResult.Ok(new ProbeTransport { Proxy = proxy, UseSystemProxy = proxy is null }, _snapshot);
    }

    public override Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken)
    {
        _snapshot = null;
        return Task.FromResult(RollbackResult.Ok());
    }
}
