using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NetworkOptimizer.Core;
using NetworkOptimizer.Network;

namespace NetworkOptimizer.Probes;

public sealed class ConnectivityProbe : IConnectivityProbe
{
    private readonly AppConfiguration _config;
    private readonly SimpleDnsClient _dns = new();

    public ConnectivityProbe(AppConfiguration config)
    {
        _config = config;
    }

    public async Task<CombinedProbeResult> ProbeAsync(ProbeTransport transport, CancellationToken cancellationToken)
    {
        var youtube = ProbeYouTubeAsync(transport, cancellationToken);
        var discord = ProbeDiscordAsync(transport, cancellationToken);
        await Task.WhenAll(youtube, discord);
        return new CombinedProbeResult
        {
            YouTube = youtube.Result,
            Discord = discord.Result
        };
    }

    public Task<ServiceProbeResult> ProbeYouTubeAsync(ProbeTransport transport, CancellationToken ct) =>
        ProbeServiceAsync("YouTube", new[]
        {
            "https://www.youtube.com/generate_204",
            "https://www.youtube.com/",
            "https://www.google.com/"
        }, transport, accept204: true, ct);

    public Task<ServiceProbeResult> ProbeDiscordAsync(ProbeTransport transport, CancellationToken ct) =>
        ProbeServiceAsync("Discord", new[]
        {
            "https://discord.com/api/v10/gateway",
            "https://discord.com/"
        }, transport, accept204: false, ct);

    private async Task<ServiceProbeResult> ProbeServiceAsync(
        string name,
        IReadOnlyList<string> urls,
        ProbeTransport transport,
        bool accept204,
        CancellationToken ct)
    {
        Exception? last = null;
        transport.OnStep?.Invoke($"{name}  start");
        foreach (var url in urls)
        {
            try
            {
                return await ProbeUrlAsync(name, new Uri(url), transport, accept204, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        return new ServiceProbeResult
        {
            Service = name,
            Dns = LayerResult.Fail("All endpoints failed"),
            Http = LayerResult.Fail(UserFacing(last))
        };
    }

    private async Task<ServiceProbeResult> ProbeUrlAsync(
        string service,
        Uri uri,
        ProbeTransport transport,
        bool accept204,
        CancellationToken ct)
    {
        var host = uri.Host;
        var port = uri.Port <= 0 ? 443 : uri.Port;
        var sw = Stopwatch.StartNew();
        transport.OnStep?.Invoke($"{service}  DNS  {host}");

        LayerResult dns;
        IPAddress[] addresses;
        try
        {
            addresses = await ResolveAsync(host, transport, ct);
            dns = addresses.Length == 0
                ? LayerResult.Fail("DNS returned no records")
                : LayerResult.Success((int)sw.ElapsedMilliseconds);
            transport.OnStep?.Invoke(dns.Ok
                ? $"{service}  DNS  ok  {dns.LatencyMs} ms  ({addresses.Length} addr)"
                : $"{service}  DNS  fail  {dns.Reason}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = LayerResult.Fail("DNS timed out", OperationStatus.Timeout),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = LayerResult.Fail(UserFacing(ex), MapStatus(ex)),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }

        if (addresses.Length == 0)
        {
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = LayerResult.Fail("No addresses to connect"),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }

        addresses = FilterFamily(addresses, transport.AddressFamily);
        if (addresses.Length == 0)
        {
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = LayerResult.Fail(transport.AddressFamily == AddressFamilyPreference.IPv6
                    ? "No IPv6 address"
                    : "No IPv4 address"),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }

        if (transport.Proxy is not null)
        {
            transport.OnStep?.Invoke($"{service}  TCP/TLS/HTTP  via proxy {transport.Proxy.Host}:{transport.Proxy.Port}");
            return await ProbeViaProxyAsync(service, uri, transport, dns, sw, accept204, ct);
        }

        transport.OnStep?.Invoke($"{service}  TCP  {host}:{port}");
        var tcpStart = sw.ElapsedMilliseconds;
        Socket? socket = null;
        LayerResult tcp;
        try
        {
            socket = await ConnectTcpAsync(addresses, port, transport, ct);
            tcp = LayerResult.Success((int)(sw.ElapsedMilliseconds - tcpStart));
            transport.OnStep?.Invoke($"{service}  TCP  ok  {tcp.LatencyMs} ms");
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            transport.OnStep?.Invoke($"{service}  TCP  fail  {UserFacing(ex)}");
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = LayerResult.Fail(UserFacing(ex), MapStatus(ex)),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }

        transport.OnStep?.Invoke($"{service}  TLS  handshake");
        var tlsStart = sw.ElapsedMilliseconds;
        SslStream? ssl = null;
        LayerResult tls;
        try
        {
            ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), false);
            using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tlsCts.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, tlsCts.Token);
            tls = LayerResult.Success((int)(sw.ElapsedMilliseconds - tlsStart));
            transport.OnStep?.Invoke($"{service}  TLS  ok  {tls.LatencyMs} ms");
        }
        catch (Exception ex)
        {
            ssl?.Dispose();
            socket.Dispose();
            transport.OnStep?.Invoke($"{service}  TLS  fail  {UserFacing(ex)}");
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = tcp,
                Tls = LayerResult.Fail(UserFacing(ex), MapStatus(ex)),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }

        transport.OnStep?.Invoke($"{service}  HTTP  GET {uri.PathAndQuery}");
        var httpStart = sw.ElapsedMilliseconds;
        try
        {
            var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            var request = $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: NetworkOptimizer/1.0\r\nAccept: */*\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(request);
            using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            httpCts.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.RequestSeconds));
            await ssl.WriteAsync(bytes, httpCts.Token);
            var buf = new byte[1024];
            var n = await ssl.ReadAsync(buf.AsMemory(0, buf.Length), httpCts.Token);
            var header = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n));
            var ok = header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ||
                     header.StartsWith("HTTP/1.0 200", StringComparison.Ordinal) ||
                     header.StartsWith("HTTP/2", StringComparison.Ordinal) ||
                     (accept204 && (header.Contains(" 204 ") || header.StartsWith("HTTP/1.1 204") || header.StartsWith("HTTP/1.0 204"))) ||
                     header.StartsWith("HTTP/1.1 301", StringComparison.Ordinal) ||
                     header.StartsWith("HTTP/1.1 302", StringComparison.Ordinal) ||
                     header.StartsWith("HTTP/1.1 307", StringComparison.Ordinal) ||
                     header.StartsWith("HTTP/1.1 308", StringComparison.Ordinal);
            var http = ok
                ? LayerResult.Success((int)(sw.ElapsedMilliseconds - httpStart))
                : LayerResult.Fail(ParseHttpReason(header));
            transport.OnStep?.Invoke(http.Ok
                ? $"{service}  HTTP  ok  {http.LatencyMs} ms"
                : $"{service}  HTTP  fail  {http.Reason}");
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = tcp,
                Tls = tls,
                Http = http,
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            transport.OnStep?.Invoke($"{service}  HTTP  fail  {UserFacing(ex)}");
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = tcp,
                Tls = tls,
                Http = LayerResult.Fail(UserFacing(ex), MapStatus(ex)),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }
        finally
        {
            ssl.Dispose();
        }
    }

    private async Task<ServiceProbeResult> ProbeViaProxyAsync(
        string service,
        Uri uri,
        ProbeTransport transport,
        LayerResult dns,
        Stopwatch sw,
        bool accept204,
        CancellationToken ct)
    {
        try
        {
            using var handler = CreateHandler(transport);
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(_config.Timeouts.RequestSeconds)
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", "NetworkOptimizer/1.0");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            var httpStart = sw.ElapsedMilliseconds;
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            var ok = resp.IsSuccessStatusCode || code is 204 or 301 or 302 or 307 or 308 || (accept204 && code == 204);
            var http = ok
                ? LayerResult.Success((int)(sw.ElapsedMilliseconds - httpStart))
                : LayerResult.Fail($"HTTP {code}");
            transport.OnStep?.Invoke(http.Ok
                ? $"{service}  HTTP  via proxy  ok  {http.LatencyMs} ms"
                : $"{service}  HTTP  via proxy  fail  HTTP {code}");
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = ok ? LayerResult.Success(0) : LayerResult.Success(0),
                Tls = ok ? LayerResult.Success(0) : LayerResult.Success(0),
                Http = http,
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            transport.OnStep?.Invoke($"{service}  HTTP  via proxy  fail  {UserFacing(ex)}");
            return new ServiceProbeResult
            {
                Service = service,
                Endpoint = uri.Host,
                Dns = dns,
                Tcp = LayerResult.Fail(UserFacing(ex), MapStatus(ex)),
                Http = LayerResult.Fail(UserFacing(ex), MapStatus(ex)),
                TotalMs = (int)sw.ElapsedMilliseconds
            };
        }
    }

    private SocketsHttpHandler CreateHandler(ProbeTransport transport)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds),
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromSeconds(5),
            UseProxy = transport.Proxy is not null || transport.UseSystemProxy
        };
        if (transport.Proxy is not null)
        {
            handler.Proxy = new WebProxy(transport.Proxy) { BypassProxyOnLocal = transport.BypassProxyOnLocal };
        }

        if (transport.AddressFamily != AddressFamilyPreference.Any || transport.DnsServers is { Count: > 0 } ||
            !string.IsNullOrWhiteSpace(transport.BindAddress))
        {
            handler.ConnectCallback = async (ctx, ct) =>
            {
                var family = transport.AddressFamily switch
                {
                    AddressFamilyPreference.IPv4 => AddressFamily.InterNetwork,
                    AddressFamilyPreference.IPv6 => AddressFamily.InterNetworkV6,
                    _ => AddressFamily.InterNetwork
                };
                IPAddress[] addrs;
                if (transport.DnsServers is { Count: > 0 })
                {
                    addrs = (await _dns.ResolveAsync(ctx.DnsEndPoint.Host, transport.DnsServers, family,
                        TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds), ct)).ToArray();
                }
                else
                {
                    addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    addrs = FilterFamily(addrs, transport.AddressFamily);
                }

                if (addrs.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                var socket = new Socket(addrs[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                if (!string.IsNullOrWhiteSpace(transport.BindAddress) &&
                    IPAddress.TryParse(transport.BindAddress, out var bind))
                {
                    socket.Bind(new IPEndPoint(bind, 0));
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds));
                await socket.ConnectAsync(new IPEndPoint(addrs[0], ctx.DnsEndPoint.Port), timeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            };
        }

        return handler;
    }

    private async Task<IPAddress[]> ResolveAsync(string host, ProbeTransport transport, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds));
        if (transport.DnsServers is { Count: > 0 })
        {
            var family = transport.AddressFamily == AddressFamilyPreference.IPv6
                ? AddressFamily.InterNetworkV6
                : AddressFamily.InterNetwork;
            var found = await _dns.ResolveAsync(host, transport.DnsServers, family,
                TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds), timeout.Token);
            return found.ToArray();
        }

        var all = await Dns.GetHostAddressesAsync(host, timeout.Token);
        return FilterFamily(all, transport.AddressFamily);
    }

    private async Task<Socket> ConnectTcpAsync(IReadOnlyList<IPAddress> addresses, int port, ProbeTransport transport, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var address in addresses.Take(3))
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                if (!string.IsNullOrWhiteSpace(transport.BindAddress) &&
                    IPAddress.TryParse(transport.BindAddress, out var bind) &&
                    bind.AddressFamily == address.AddressFamily)
                {
                    socket.Bind(new IPEndPoint(bind, 0));
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.ConnectSeconds));
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
                return socket;
            }
            catch (Exception ex)
            {
                last = ex;
                socket?.Dispose();
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static IPAddress[] FilterFamily(IPAddress[] addresses, AddressFamilyPreference pref) => pref switch
    {
        AddressFamilyPreference.IPv4 => addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray(),
        AddressFamilyPreference.IPv6 => addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal).ToArray(),
        _ => addresses
    };

    private static OperationStatus MapStatus(Exception ex) => ex switch
    {
        TimeoutException => OperationStatus.Timeout,
        OperationCanceledException => OperationStatus.Timeout,
        SocketException sock when sock.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock => OperationStatus.Timeout,
        _ => OperationStatus.Failed
    };

    private static string UserFacing(Exception? ex) => ex switch
    {
        null => "Request failed",
        TimeoutException => "Timed out",
        OperationCanceledException => "Timed out",
        SocketException sock => sock.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Connection refused",
            SocketError.TimedOut => "Timed out",
            SocketError.HostUnreachable => "Host unreachable",
            SocketError.NetworkUnreachable => "Network unreachable",
            SocketError.HostNotFound => "Host not found",
            _ => "Connection failed"
        },
        AuthenticationException => "TLS handshake failed",
        HttpRequestException => "HTTP request failed",
        _ => "Connection failed"
    };

    private static string ParseHttpReason(string header)
    {
        var first = header.Split('\n')[0].Trim();
        if (string.IsNullOrWhiteSpace(first)) return "Empty HTTP response";
        return first.Length <= 80 ? first : first[..77] + "...";
    }
}
