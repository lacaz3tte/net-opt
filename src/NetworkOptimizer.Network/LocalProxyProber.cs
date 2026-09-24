using System.Net.Sockets;
using System.Text;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Network;

public sealed class LocalProxyProber
{
    private static readonly int[] WellKnownPorts =
    {
        1080, 1081, 10808, 10809, 7890, 7891, 7892, 8080, 8888, 2080, 9050, 3128, 8118,
        20170, 12334, 6152, 6153, 2087, 1087, 1090, 12345, 18080, 7897, 20171, 3067
    };

    private readonly int _timeoutMs;

    public LocalProxyProber(int timeoutMs)
    {
        _timeoutMs = Math.Clamp(timeoutMs, 100, 5000);
    }

    public static IReadOnlyList<int> SelectCandidatePorts(IReadOnlyList<int> listening, IReadOnlyList<InstalledTool> tools)
    {
        var set = new HashSet<int>();
        foreach (var port in listening)
        {
            if (WellKnownPorts.Contains(port)) set.Add(port);
        }

        foreach (var tool in tools.Where(t => t.Present))
        {
            foreach (var port in tool.ListeningPorts)
            {
                if (port > 0) set.Add(port);
            }
        }

        // If a known tool is running, also probe well-known ports even if GetActiveTcpListeners missed them.
        if (tools.Any(t => t.Present))
        {
            foreach (var port in WellKnownPorts) set.Add(port);
        }

        return set.OrderBy(p => p).ToArray();
    }

    public async Task<IReadOnlyList<ProxyEndpoint>> ProbeAsync(IReadOnlyList<int> ports, CancellationToken ct)
    {
        var results = new List<ProxyEndpoint>();
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            var socks5 = await ProbeSocksAsync("127.0.0.1", port, socks5: true, ct);
            if (socks5.HandshakeOk)
            {
                results.Add(socks5);
                continue;
            }

            var socks4 = await ProbeSocksAsync("127.0.0.1", port, socks5: false, ct);
            if (socks4.HandshakeOk)
            {
                results.Add(socks4);
                continue;
            }

            var http = await ProbeHttpConnectAsync("127.0.0.1", port, ct);
            if (http.HandshakeOk)
            {
                results.Add(http);
            }
        }

        return results;
    }

    public async Task<ProxyEndpoint> ProbeSocksAsync(string host, int port, bool socks5, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            stream.ReadTimeout = _timeoutMs;
            stream.WriteTimeout = _timeoutMs;

            if (socks5)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeout.Token);
                var buf = new byte[2];
                var n = await ReadExactAsync(stream, buf, 2, timeout.Token);
                if (n >= 2 && buf[0] == 0x05 && (buf[1] == 0x00 || buf[1] == 0x02))
                {
                    return Ok(host, port, "socks5", "SOCKS5 greeting accepted");
                }

                return Fail(host, port, "socks5", "Unexpected SOCKS5 greeting");
            }

            // SOCKS4 connect to 127.0.0.1:1 — we only check that a reply starts with 0x00 0x5a/0x5b
            var req = new byte[] { 0x04, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0 };
            await stream.WriteAsync(req, timeout.Token);
            var reply = new byte[8];
            var read = await ReadExactAsync(stream, reply, 2, timeout.Token);
            if (read >= 2 && reply[0] == 0x00 && (reply[1] == 0x5a || reply[1] == 0x5b || reply[1] == 0x5c || reply[1] == 0x5d))
            {
                return Ok(host, port, "socks4", "SOCKS4 reply received");
            }

            return Fail(host, port, "socks4", "Not a SOCKS4 endpoint");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(host, port, socks5 ? "socks5" : "socks4", "Handshake timed out");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return Fail(host, port, socks5 ? "socks5" : "socks4", "Connection refused");
        }
        catch
        {
            return Fail(host, port, socks5 ? "socks5" : "socks4", "Handshake failed");
        }
    }

    public async Task<ProxyEndpoint> ProbeHttpConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            var req = Encoding.ASCII.GetBytes("CONNECT example.invalid:443 HTTP/1.1\r\nHost: example.invalid:443\r\nProxy-Connection: close\r\n\r\n");
            await stream.WriteAsync(req, timeout.Token);
            var buf = new byte[128];
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), timeout.Token);
            var text = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n));
            if (text.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(host, port, "http", "HTTP CONNECT response received");
            }

            return Fail(host, port, "http", "Not an HTTP proxy");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(host, port, "http", "Handshake timed out");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return Fail(host, port, "http", "Connection refused");
        }
        catch
        {
            return Fail(host, port, "http", "Handshake failed");
        }
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n <= 0) break;
            read += n;
        }

        return read;
    }

    private static ProxyEndpoint Ok(string host, int port, string kind, string reason) => new()
    {
        Host = host,
        Port = port,
        Kind = kind,
        Source = "local-probe",
        HandshakeOk = true,
        HandshakeReason = reason
    };

    private static ProxyEndpoint Fail(string host, int port, string kind, string reason) => new()
    {
        Host = host,
        Port = port,
        Kind = kind,
        Source = "local-probe",
        HandshakeOk = false,
        HandshakeReason = reason
    };
}
