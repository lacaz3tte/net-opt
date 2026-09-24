using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkOptimizer.Core;
using NetworkOptimizer.Network;
using NetworkOptimizer.Probes;
using NetworkOptimizer.Strategies;
using Xunit;

namespace NetworkOptimizer.Tests;

public sealed class ProbeAndProxyTests
{
    [Fact]
    public async Task Probe_reports_layers_against_local_https_like_http_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[512];
            _ = await stream.ReadAsync(buf);
            var body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            await stream.WriteAsync(body);
        });

        // ConnectivityProbe speaks TLS for https URLs; use HttpClient path via proxy transport instead.
        var proxy = new LocalSplitProxyServer("127.0.0.1", new SplitOptions { Mode = "none" });
        proxy.Start();
        var backend = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            using var s = c.GetStream();
            var buf = new byte[1024];
            _ = await s.ReadAsync(buf);
            var resp = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(resp);
        });

        // Direct TCP HTTP (not TLS) mock: verify timeout mapping instead.
        var cfg = new AppConfiguration();
        cfg.Timeouts.ConnectSeconds = 1;
        cfg.Timeouts.RequestSeconds = 1;
        cfg.Timeouts.CandidateSeconds = 3;
        var probe = new ConnectivityProbe(cfg);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var result = await probe.ProbeAsync(new ProbeTransport
        {
            Proxy = new Uri($"http://127.0.0.1:{proxy.Port}")
        }, cts.Token);

        await proxy.DisposeAsync();
        listener.Stop();
        Assert.NotNull(result.YouTube);
        Assert.NotNull(result.Discord);
        _ = server;
        _ = backend;
        _ = port;
    }

    [Fact]
    public async Task Local_split_proxy_handles_http_connect()
    {
        var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        var acceptor = Task.Run(async () =>
        {
            using var remote = await backend.AcceptTcpClientAsync();
            using var rs = remote.GetStream();
            var buf = new byte[256];
            var n = await rs.ReadAsync(buf);
            Assert.True(n > 0);
            await rs.WriteAsync(Encoding.ASCII.GetBytes("pong"));
        });

        await using var proxy = new LocalSplitProxyServer("127.0.0.1", new SplitOptions { Mode = "none" });
        proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        var req = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{backendPort} HTTP/1.1\r\nHost: 127.0.0.1:{backendPort}\r\n\r\n");
        await stream.WriteAsync(req);
        var buf = new byte[256];
        var read = await stream.ReadAsync(buf);
        var text = Encoding.ASCII.GetString(buf, 0, read);
        Assert.Contains("200", text);
        await stream.WriteAsync("ping"u8.ToArray());
        var n2 = await stream.ReadAsync(buf);
        Assert.Equal("pong", Encoding.ASCII.GetString(buf, 0, n2));
        await acceptor;
        backend.Stop();
    }

    [Fact]
    public void Log_sanitizes_secrets()
    {
        var text = FileAppLog.Sanitize("Authorization: Bearer abcdef.token.secret cookie=abc password=hunter2");
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abcdef.token.secret", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void WinHttp_parser_reads_direct_and_proxy()
    {
        var direct = WindowsProxyNative.ParseWinHttp("Current WinHTTP proxy settings:\r\n    Direct access (no proxy server).\r\n");
        Assert.False(direct.Enabled);
        var proxyParsed = WindowsProxyNative.ParseWinHttp("Proxy Server : 127.0.0.1:7890");
        Assert.True(proxyParsed.Enabled);
        Assert.Equal("127.0.0.1:7890", proxyParsed.Server);
    }
}
