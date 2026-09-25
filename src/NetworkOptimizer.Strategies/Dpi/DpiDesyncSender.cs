using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetworkOptimizer.Strategies;

/// <summary>
/// Userspace TCP desync similar to zapret split2 / GoodbyeDPI -e/-s and ByeDPI.
/// Forces the ClientHello onto separate segments so ISP DPI cannot reassemble SNI
/// in the first packet. Does not install drivers or send traffic to other hosts.
/// </summary>
public static class DpiDesyncSender
{
    public static async Task SendAsync(Socket socket, ReadOnlyMemory<byte> data, SplitOptions options, CancellationToken ct)
    {
        if (data.Length == 0) return;
        socket.NoDelay = true;

        if (options.Mode.Equals("none", StringComparison.OrdinalIgnoreCase) || data.Length < 2)
        {
            await SendAllAsync(socket, data, ct);
            return;
        }

        if (options.OutOfBand)
        {
            TrySendOob(socket);
        }

        foreach (var part in BuildParts(data.ToArray(), options))
        {
            await SendSegmentAsync(socket, part, options, ct);
        }
    }

    private static List<byte[]> BuildParts(byte[] buffer, SplitOptions options)
    {
        if (options.TlsRecordSplit &&
            TlsClientHello.TryFragmentRecord(buffer, PayloadSplit(buffer, options), out var rec1, out var rec2))
        {
            return new List<byte[]> { rec1, rec2 };
        }

        if (options.MultiSplit)
        {
            var p1 = Math.Clamp(options.Position <= 0 ? 1 : options.Position, 1, buffer.Length - 2);
            var p2 = TlsClientHello.ResolveSplitPosition(buffer, "midsld", Math.Min(buffer.Length / 2, buffer.Length - 1));
            if (p2 <= p1) p2 = Math.Min(buffer.Length - 1, p1 + Math.Max(1, (buffer.Length - p1) / 2));
            return new List<byte[]>
            {
                buffer[..p1],
                buffer[p1..p2],
                buffer[p2..]
            };
        }

        var pos = options.SniAware
            ? TlsClientHello.ResolveSplitPosition(buffer, options.SplitAt ?? "midsld", options.Position)
            : Math.Clamp(options.Position <= 0 ? 2 : options.Position, 1, buffer.Length - 1);
        return new List<byte[]> { buffer[..pos], buffer[pos..] };
    }

    private static int PayloadSplit(ReadOnlySpan<byte> record, SplitOptions options)
    {
        var abs = options.SniAware
            ? TlsClientHello.ResolveSplitPosition(record, options.SplitAt ?? "midsld", options.Position)
            : Math.Clamp(options.Position <= 0 ? 2 : options.Position, 1, Math.Max(1, record.Length - 1));
        return Math.Clamp(abs - 5, 1, Math.Max(1, record.Length - 6));
    }

    private static async Task SendSegmentAsync(Socket socket, ReadOnlyMemory<byte> part, SplitOptions options, CancellationToken ct)
    {
        ulong? before = options.WaitForAck ? TryReadBytesOut(socket) : null;
        await SendAllAsync(socket, part, ct);
        DrainSendBuffer(socket);
        if (options.WaitForAck)
        {
            await WaitForAckOrDelayAsync(socket, before, options.DelayMs, ct);
        }
        else if (options.DelayMs > 0)
        {
            await Task.Delay(options.DelayMs, ct);
        }
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var sent = 0;
        while (sent < data.Length)
        {
            var n = await socket.SendAsync(data[sent..], SocketFlags.None, ct);
            if (n <= 0) throw new IOException("socket send returned 0");
            sent += n;
        }
    }

    private static void DrainSendBuffer(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
        }
        catch
        {
            // ignore
        }
    }

    private static void TrySendOob(Socket socket)
    {
        try
        {
            socket.Send(new byte[] { 0x16 }, SocketFlags.OutOfBand);
        }
        catch
        {
            // OOB is a best-effort DPI confuse; skip if the stack rejects it.
        }
    }

    private static async Task WaitForAckOrDelayAsync(Socket socket, ulong? bytesOutBefore, int delayMs, CancellationToken ct)
    {
        var budget = Math.Clamp(delayMs > 0 ? delayMs : 40, 10, 250);
        if (bytesOutBefore is ulong before && OperatingSystem.IsWindows())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < budget)
            {
                ct.ThrowIfCancellationRequested();
                var info = TryReadTcpInfo(socket);
                if (info is { } tcp && tcp.BytesOut > before && tcp.BytesInFlight == 0)
                {
                    return;
                }

                await Task.Delay(4, ct);
            }
        }

        await Task.Delay(budget, ct);
    }

    private static ulong? TryReadBytesOut(Socket socket)
    {
        var info = TryReadTcpInfo(socket);
        return info?.BytesOut;
    }

    private static TcpInfoV0? TryReadTcpInfo(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var input = BitConverter.GetBytes(0);
            var output = new byte[128];
            var n = socket.IOControl(unchecked((int)0xD8000027), input, output);
            if (n < 88) return null;
            return MemoryMarshal.Read<TcpInfoV0>(output);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TcpInfoV0
    {
        public uint State;
        public uint Mss;
        public ulong ConnectionTimeMs;
        public byte TimestampsEnabled;
        public uint RttUs;
        public uint MinRttUs;
        public uint BytesInFlight;
        public uint Cwnd;
        public uint SndWnd;
        public uint RcvWnd;
        public uint RcvBuf;
        public ulong BytesOut;
        public ulong BytesIn;
        public uint BytesReordered;
        public uint BytesRetrans;
        public uint FastRetrans;
        public uint DupAcksIn;
        public uint TimeoutEpisodes;
        public byte SynRetrans;
    }
}
