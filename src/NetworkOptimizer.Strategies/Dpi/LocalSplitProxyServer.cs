using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Strategies;

public sealed class SplitOptions
{
    public string Mode { get; init; } = "clienthello";
    public int Position { get; init; } = 2;
    public int DelayMs { get; init; } = 1;
}

/// <summary>
/// Local HTTP CONNECT + SOCKS5 proxy that only handles this machine's traffic.
/// Optional TLS ClientHello splitting is a local transport tweak, not an exploit.
/// </summary>
public sealed class LocalSplitProxyServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly SplitOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _started;

    public IPEndPoint EndPoint { get; }
    public int Port => EndPoint.Port;

    public LocalSplitProxyServer(string listenAddress, SplitOptions options)
    {
        _options = options;
        var ip = IPAddress.Parse(listenAddress);
        _listener = new TcpListener(ip, 0);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        EndPoint = (IPEndPoint)_listener.LocalEndpoint;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            stream.WriteTimeout = 15000;
            var first = new byte[1];
            int n;
            try
            {
                n = await stream.ReadAsync(first.AsMemory(0, 1), ct);
            }
            catch
            {
                return;
            }

            if (n <= 0) return;

            try
            {
                if (first[0] == 0x05)
                {
                    await HandleSocks5Async(stream, ct);
                }
                else
                {
                    await HandleHttpAsync(stream, first[0], ct);
                }
            }
            catch
            {
                // drop connection
            }
        }
    }

    private async Task HandleSocks5Async(NetworkStream client, CancellationToken ct)
    {
        var methodsLen = new byte[1];
        if (await client.ReadAsync(methodsLen.AsMemory(0, 1), ct) <= 0) return;
        var methods = new byte[methodsLen[0]];
        if (methods.Length > 0)
        {
            _ = await ReadAtLeastAsync(client, methods, methods.Length, ct);
        }

        await client.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

        var header = new byte[4];
        if (await ReadAtLeastAsync(client, header, 4, ct) < 4) return;
        if (header[0] != 0x05 || header[1] != 0x01)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }

        string host;
        int port;
        switch (header[3])
        {
            case 0x01:
                var ipv4 = new byte[4];
                await ReadAtLeastAsync(client, ipv4, 4, ct);
                host = new IPAddress(ipv4).ToString();
                break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadAtLeastAsync(client, lenBuf, 1, ct);
                var name = new byte[lenBuf[0]];
                await ReadAtLeastAsync(client, name, name.Length, ct);
                host = Encoding.ASCII.GetString(name);
                break;
            case 0x04:
                var ipv6 = new byte[16];
                await ReadAtLeastAsync(client, ipv6, 16, ct);
                host = new IPAddress(ipv6).ToString();
                break;
            default:
                await client.WriteAsync(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                return;
        }

        var portBuf = new byte[2];
        await ReadAtLeastAsync(client, portBuf, 2, ct);
        port = (portBuf[0] << 8) | portBuf[1];

        using var remote = await ConnectRemoteAsync(host, port, ct);
        if (remote is null)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }

        await client.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        await PipeAsync(client, remote.GetStream(), ct);
    }

    private async Task HandleHttpAsync(NetworkStream client, byte firstByte, CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.Append((char)firstByte);
        var buf = new byte[1];
        while (!builder.ToString().Contains("\r\n\r\n") && builder.Length < 8192)
        {
            var n = await client.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n <= 0) return;
            builder.Append((char)buf[0]);
        }

        var header = builder.ToString();
        var firstLine = header.Split('\n')[0].Trim();
        if (firstLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
        {
            var target = firstLine.Split(' ')[1];
            var host = target;
            var port = 443;
            var colon = target.LastIndexOf(':');
            if (colon > 0 && int.TryParse(target[(colon + 1)..], out var parsed))
            {
                host = target[..colon];
                port = parsed;
            }

            using var remote = await ConnectRemoteAsync(host, port, ct);
            if (remote is null)
            {
                var fail = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                await client.WriteAsync(fail, ct);
                return;
            }

            var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: NetworkOptimizer\r\n\r\n");
            await client.WriteAsync(ok, ct);
            await PipeAsync(client, remote.GetStream(), ct);
            return;
        }

        var deny = Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await client.WriteAsync(deny, ct);
    }

    private async Task<TcpClient?> ConnectRemoteAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, port, timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            return null;
        }
    }

    private async Task PipeAsync(NetworkStream client, NetworkStream remote, CancellationToken ct)
    {
        var up = PumpAsync(client, remote, splitTls: true, ct);
        var down = PumpAsync(remote, client, splitTls: false, ct);
        await Task.WhenAny(up, down);
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, bool splitTls, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            }
            catch
            {
                break;
            }

            if (n <= 0) break;

            try
            {
                if (first && splitTls && ShouldSplit(buffer, n))
                {
                    var pos = Math.Clamp(_options.Position, 1, Math.Max(1, n - 1));
                    await to.WriteAsync(buffer.AsMemory(0, pos), ct);
                    await to.FlushAsync(ct);
                    if (_options.DelayMs > 0)
                    {
                        await Task.Delay(_options.DelayMs, ct);
                    }

                    await to.WriteAsync(buffer.AsMemory(pos, n - pos), ct);
                    await to.FlushAsync(ct);
                }
                else
                {
                    await to.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            catch
            {
                break;
            }

            first = false;
        }
    }

    private bool ShouldSplit(byte[] buffer, int length)
    {
        if (_options.Mode.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        if (length < 6) return false;
        // TLS handshake record: 0x16 0x03 0x01-0x04
        return buffer[0] == 0x16 && buffer[1] == 0x03;
    }

    private static async Task<int> ReadAtLeastAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n <= 0) return read;
            read += n;
        }

        return read;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        }

        _cts.Dispose();
    }
}
