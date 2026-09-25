using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkOptimizer.Core;
using NetworkOptimizer.Strategies;
using Xunit;

namespace NetworkOptimizer.Tests;

public sealed class DpiDesyncTests
{
    [Fact]
    public void Parser_finds_sni_and_midsld_inside_youtube()
    {
        var hello = BuildClientHello("www.youtube.com");
        Assert.True(TlsClientHello.TryParse(hello, out var info));
        Assert.True(info.IsTlsHandshake);
        Assert.Equal("www.youtube.com", info.SniHost);
        Assert.Equal(7, TlsClientHello.MidSldOffset("www.youtube.com"));
        var pos = TlsClientHello.ResolveSplitPosition(hello, "midsld", 2);
        Assert.True(pos > 5);
        Assert.True(pos < hello.Length - 1);
        Assert.Equal('t', (char)hello[info.SniStart!.Value + 7]);
    }

    [Fact]
    public void Tls_record_fragment_reassembles_to_original_payload()
    {
        var hello = BuildClientHello("discord.com");
        Assert.True(TlsClientHello.TryFragmentRecord(hello, 8, out var a, out var b));
        Assert.Equal(0x16, a[0]);
        Assert.Equal(0x16, b[0]);
        var payload = hello.AsSpan(5).ToArray();
        var joined = a.Skip(5).Concat(b.Skip(5)).ToArray();
        Assert.Equal(payload, joined);
    }

    [Fact]
    public void Dpi_desync_strategy_queues_zapret_like_stage1()
    {
        var strategy = new DpiDesyncStrategy(new FakeStore(), new AppConfiguration());
        strategy.Bind(TestEnv.SampleDiscovery());
        Assert.True(strategy.IsAvailable());
        var list = strategy.GenerateCandidates().ToList();
        Assert.Contains(list, c => c.Stage == 1 && c.DisplayName.Contains("midsld", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, c => c.Stage == 1 && c.DisplayName.Contains("GoodbyeDPI", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, c => c.Parameters.BlockQuic);
        Assert.Contains(list, c => c.Parameters.DesyncMode != null);
    }

    [Fact]
    public void Catalog_includes_dpi_desync_before_local_split()
    {
        var catalog = new StrategyCatalog(new FakeStore(), new AppConfiguration());
        var ids = catalog.Typed.Select(s => s.Id).ToList();
        Assert.Contains("dpi-desync", ids);
        Assert.True(ids.IndexOf("dpi-desync") < ids.IndexOf("dpi-local"));
    }

    [Fact]
    public async Task Desync_proxy_delivers_full_clienthello_to_backend()
    {
        var hello = BuildClientHello("www.youtube.com");
        var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        var received = new MemoryStream();
        var acceptor = Task.Run(async () =>
        {
            using var remote = await backend.AcceptTcpClientAsync();
            using var rs = remote.GetStream();
            var buf = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && received.Length < hello.Length)
            {
                if (rs.DataAvailable || received.Length == 0)
                {
                    var n = await rs.ReadAsync(buf);
                    if (n <= 0) break;
                    received.Write(buf, 0, n);
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        });

        await using var proxy = new LocalSplitProxyServer("127.0.0.1", new SplitOptions
        {
            Mode = "clienthello",
            SniAware = true,
            SplitAt = "midsld",
            WaitForAck = true,
            DelayMs = 15,
            Position = 2
        });
        proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        var req = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{backendPort} HTTP/1.1\r\nHost: 127.0.0.1:{backendPort}\r\n\r\n");
        await stream.WriteAsync(req);
        var buf = new byte[256];
        var read = await stream.ReadAsync(buf);
        Assert.Contains("200", Encoding.ASCII.GetString(buf, 0, read));
        await stream.WriteAsync(hello);
        await acceptor.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Stop();
        Assert.Equal(hello, received.ToArray());
    }

    internal static byte[] BuildClientHello(string host)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var name = new byte[3 + hostBytes.Length];
        name[0] = 0;
        name[1] = (byte)(hostBytes.Length >> 8);
        name[2] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, name, 3, hostBytes.Length);

        var list = new byte[2 + name.Length];
        list[0] = (byte)(name.Length >> 8);
        list[1] = (byte)name.Length;
        Buffer.BlockCopy(name, 0, list, 2, name.Length);

        var ext = new byte[4 + list.Length];
        ext[2] = (byte)(list.Length >> 8);
        ext[3] = (byte)list.Length;
        Buffer.BlockCopy(list, 0, ext, 4, list.Length);

        var body = new List<byte>(128);
        body.Add(0x03);
        body.Add(0x03);
        body.AddRange(new byte[32]);
        body.Add(0);
        body.Add(0);
        body.Add(2);
        body.Add(0x13);
        body.Add(0x01);
        body.Add(1);
        body.Add(0);
        body.Add((byte)(ext.Length >> 8));
        body.Add((byte)ext.Length);
        body.AddRange(ext);

        var handshake = new List<byte>(body.Count + 4) { 0x01, (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count };
        handshake.AddRange(body);

        var record = new List<byte>(handshake.Count + 5)
        {
            0x16, 0x03, 0x01, (byte)(handshake.Count >> 8), (byte)handshake.Count
        };
        record.AddRange(handshake);
        return record.ToArray();
    }
}
