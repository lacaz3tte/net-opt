using NetworkOptimizer.Core;
using NetworkOptimizer.Network;
using Xunit;

namespace NetworkOptimizer.Tests;

public sealed class ConfigurationParsingTests
{
    [Fact]
    public void Parse_empty_returns_defaults()
    {
        var cfg = ConfigurationStore.Parse("");
        Assert.Equal(5, cfg.Timeouts.ConnectSeconds);
        Assert.Equal(10, cfg.Timeouts.RequestSeconds);
        Assert.Equal(20, cfg.Timeouts.CandidateSeconds);
        Assert.Equal(SearchMode.Fast, cfg.Search.Mode);
    }

    [Fact]
    public void Parse_overrides_known_fields()
    {
        var json = """
        {
          "timeouts": { "connectSeconds": 3, "requestSeconds": 7, "candidateSeconds": 15 },
          "search": { "mode": "Best", "maxCandidates": 12, "allowDnsChanges": false },
          "monitor": { "intervalSeconds": 30, "autoRediscover": false }
        }
        """;
        var cfg = ConfigurationStore.Parse(json);
        Assert.Equal(3, cfg.Timeouts.ConnectSeconds);
        Assert.Equal(7, cfg.Timeouts.RequestSeconds);
        Assert.Equal(15, cfg.Timeouts.CandidateSeconds);
        Assert.Equal(SearchMode.Best, cfg.Search.Mode);
        Assert.Equal(12, cfg.Search.MaxCandidates);
        Assert.False(cfg.Search.AllowDnsChanges);
        Assert.Equal(30, cfg.Monitor.IntervalSeconds);
        Assert.False(cfg.Monitor.AutoRediscover);
    }

    [Fact]
    public void Parse_clamps_out_of_range_values()
    {
        var json = """
        { "timeouts": { "connectSeconds": 9999, "candidateSeconds": 0 },
          "search": { "maxCandidates": 0 } }
        """;
        var cfg = ConfigurationStore.Parse(json);
        Assert.Equal(5, cfg.Timeouts.ConnectSeconds);
        Assert.Equal(20, cfg.Timeouts.CandidateSeconds);
        Assert.Equal(48, cfg.Search.MaxCandidates);
    }
}

public sealed class ScoringTests
{
    [Fact]
    public void Full_success_scores_youtube_and_discord()
    {
        var score = Scorer.Score(FakeProbe.Ok(400, 180), stable: true);
        Assert.Equal(40, score.YouTubeAvailable);
        Assert.Equal(40, score.DiscordAvailable);
        Assert.Equal(10, score.StableRepeatedTest);
        Assert.Equal(10, score.LowLatency);
        Assert.Equal(100, score.Total);
        Assert.Equal(OperationStatus.Success, Scorer.Classify(FakeProbe.Ok()));
    }

    [Fact]
    public void Youtube_only_is_partial()
    {
        var probe = FakeProbe.YoutubeOnly();
        var score = Scorer.Score(probe, stable: false);
        Assert.Equal(40, score.YouTubeAvailable);
        Assert.Equal(0, score.DiscordAvailable);
        Assert.Equal(OperationStatus.Partial, Scorer.Classify(probe));
    }

    [Fact]
    public void Both_down_is_failed_with_zero_service_points()
    {
        var probe = FakeProbe.Fail("Connection refused");
        var score = Scorer.Score(probe, true);
        Assert.Equal(0, score.Total);
        Assert.Equal(OperationStatus.Failed, Scorer.Classify(probe));
    }
}

public sealed class CandidateGenerationTests
{
    [Fact]
    public void Generator_skips_unavailable_and_ranks_stage1_first()
    {
        var a = new ScriptedStrategy { Id = "a", Name = "A", Available = true };
        a.Add("a1", rank: 2, stage: 1);
        a.Add("a2", rank: 1, stage: 2);
        var b = new ScriptedStrategy { Id = "b", Name = "B", Available = false };
        b.Add("b1");
        var c = new ScriptedStrategy { Id = "c", Name = "C", Available = true };
        c.Add("c1", rank: 1, stage: 1);

        var gen = new CandidateGenerator(new AppConfiguration());
        var stage1 = gen.Generate(new INetworkStrategy[] { a, b, c }, TestEnv.SampleDiscovery(), 1);
        Assert.Equal(2, stage1.Count);
        Assert.All(stage1, x => Assert.Equal(1, x.Stage));
        Assert.DoesNotContain(stage1, x => x.StrategyId == "b");

        var stage2 = gen.Generate(new INetworkStrategy[] { a, b, c }, TestEnv.SampleDiscovery(), 2);
        Assert.Single(stage2);
        Assert.Equal("a2", stage2[0].DisplayName);
    }
}

public sealed class DiscoveryTests
{
    [Fact]
    public async Task Discovery_reports_adapters_from_store()
    {
        var store = new FakeStore();
        store.Adapters.Add(new NetworkAdapterInfo
        {
            Id = "1",
            Name = "Ethernet",
            Description = "e",
            Type = "ethernet",
            Operational = true,
            SupportsIPv4 = true,
            UnicastAddresses = new[] { "10.1.2.3" },
            Gateways = new[] { "10.1.2.1" },
            DnsServers = new[] { "1.1.1.1" }
        });
        var discovery = new DiscoveryService(store, new AppConfiguration());
        var report = await discovery.DiscoverAsync(CancellationToken.None);
        Assert.Contains(report.Adapters, a => a.Name == "Ethernet");
        Assert.True(report.HasEthernet);
        Assert.True(report.HasIPv4);
    }

    [Fact]
    public async Task Handshake_rejects_plain_tcp_listener()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync();
        var prober = new LocalProxyProber(400);
        var http = await prober.ProbeHttpConnectAsync("127.0.0.1", port, CancellationToken.None);
        Assert.False(http.HandshakeOk);
        listener.Stop();
    }

    [Fact]
    public async Task Handshake_accepts_socks5_greeting()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[3];
            _ = await s.ReadAsync(buf);
            await s.WriteAsync(new byte[] { 0x05, 0x00 });
        });

        var prober = new LocalProxyProber(800);
        var result = await prober.ProbeSocksAsync("127.0.0.1", port, socks5: true, CancellationToken.None);
        await server;
        listener.Stop();
        Assert.True(result.HandshakeOk);
        Assert.Equal("socks5", result.Kind);
    }
}
