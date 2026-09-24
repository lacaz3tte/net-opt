using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetworkOptimizer.Network;

public sealed class SimpleDnsClient
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        IReadOnlyList<string> servers,
        AddressFamily family,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new[] { literal };
        }

        Exception? last = null;
        foreach (var server in servers)
        {
            try
            {
                var records = await QueryAsync(host, server, family == AddressFamily.InterNetworkV6 ? (ushort)28 : (ushort)1, timeout, ct);
                if (records.Count > 0) return records;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
            {
                last = ex;
                if (ct.IsCancellationRequested) throw;
            }
        }

        if (last is not null) throw last;
        return Array.Empty<IPAddress>();
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(
        string host, string server, ushort type, TimeSpan timeout, CancellationToken ct)
    {
        if (!IPAddress.TryParse(server, out var serverIp))
        {
            return Array.Empty<IPAddress>();
        }

        var query = BuildQuery(host, type);
        using var udp = new UdpClient(serverIp.AddressFamily);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        await udp.SendAsync(query, new IPEndPoint(serverIp, 53));
        var result = await udp.ReceiveAsync(timeoutCts.Token);
        return ParseAnswers(result.Buffer, type);
    }

    private static byte[] BuildQuery(string host, ushort type)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = 12 + labels.Sum(l => l.Length + 1) + 1 + 4;
        var buf = new byte[length];
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), id);
        buf[2] = 0x01; // recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), 1); // QDCOUNT
        var offset = 12;
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buf[offset++] = (byte)bytes.Length;
            bytes.CopyTo(buf.AsSpan(offset));
            offset += bytes.Length;
        }

        buf[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), type);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), 1);
        return buf;
    }

    private static IReadOnlyList<IPAddress> ParseAnswers(byte[] buffer, ushort expectedType)
    {
        if (buffer.Length < 12) return Array.Empty<IPAddress>();
        var qd = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
        var an = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));
        var offset = 12;
        for (var i = 0; i < qd; i++)
        {
            SkipName(buffer, ref offset);
            offset += 4;
        }

        var list = new List<IPAddress>();
        for (var i = 0; i < an && offset + 10 <= buffer.Length; i++)
        {
            SkipName(buffer, ref offset);
            if (offset + 10 > buffer.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            offset += 8;
            var rdlength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            offset += 2;
            if (offset + rdlength > buffer.Length) break;
            if (type == expectedType)
            {
                if (type == 1 && rdlength == 4)
                {
                    list.Add(new IPAddress(buffer.AsSpan(offset, 4)));
                }
                else if (type == 28 && rdlength == 16)
                {
                    list.Add(new IPAddress(buffer.AsSpan(offset, 16)));
                }
            }

            offset += rdlength;
        }

        return list;
    }

    private static void SkipName(byte[] buffer, ref int offset)
    {
        while (offset < buffer.Length)
        {
            var len = buffer[offset];
            if (len == 0)
            {
                offset++;
                return;
            }

            if ((len & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += len + 1;
        }
    }
}
