using System.Text;

namespace NetworkOptimizer.Strategies;

/// <summary>
/// Parses a TLS ClientHello enough to locate SNI. Used to split the hostname
/// the way zapret (midsld / sniext) and GoodbyeDPI fragment HTTPS.
/// </summary>
public static class TlsClientHello
{
    public readonly record struct Info(
        bool IsTlsHandshake,
        int RecordEnd,
        int? SniStart,
        int? SniLength,
        string? SniHost);

    public static bool HasCompleteRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;
        if (data[0] != 0x16) return true;
        if (data.Length < 5) return false;
        var len = (data[3] << 8) | data[4];
        return data.Length >= 5 + len;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out Info info)
    {
        info = default;
        if (data.Length < 9 || data[0] != 0x16 || data[1] != 0x03)
        {
            return false;
        }

        var recordLen = (data[3] << 8) | data[4];
        var recordEnd = Math.Min(data.Length, 5 + recordLen);
        if (recordEnd < 6 || data[5] != 0x01)
        {
            info = new Info(true, recordEnd, null, null, null);
            return true;
        }

        var handshakeLen = (data[6] << 16) | (data[7] << 8) | data[8];
        var handshakeEnd = Math.Min(recordEnd, 9 + handshakeLen);
        var offset = 9;
        if (offset + 2 + 32 + 1 > handshakeEnd)
        {
            info = new Info(true, recordEnd, null, null, null);
            return true;
        }

        offset += 2; // client version
        offset += 32; // random
        var sessionLen = data[offset];
        offset += 1 + sessionLen;
        if (offset + 2 > handshakeEnd)
        {
            info = new Info(true, recordEnd, null, null, null);
            return true;
        }

        var cipherLen = (data[offset] << 8) | data[offset + 1];
        offset += 2 + cipherLen;
        if (offset + 1 > handshakeEnd)
        {
            info = new Info(true, recordEnd, null, null, null);
            return true;
        }

        var compLen = data[offset];
        offset += 1 + compLen;
        if (offset + 2 > handshakeEnd)
        {
            info = new Info(true, recordEnd, null, null, null);
            return true;
        }

        var extLen = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        var extEnd = Math.Min(handshakeEnd, offset + extLen);
        while (offset + 4 <= extEnd)
        {
            var type = (data[offset] << 8) | data[offset + 1];
            var len = (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            if (offset + len > extEnd) break;
            if (type == 0 && len >= 5)
            {
                var listLen = (data[offset] << 8) | data[offset + 1];
                if (listLen >= 3 && offset + 2 + listLen <= offset + len)
                {
                    var nameType = data[offset + 2];
                    var nameLen = (data[offset + 3] << 8) | data[offset + 4];
                    var nameStart = offset + 5;
                    if (nameType == 0 && nameLen > 0 && nameStart + nameLen <= offset + len)
                    {
                        var host = Encoding.ASCII.GetString(data.Slice(nameStart, nameLen));
                        info = new Info(true, recordEnd, nameStart, nameLen, host);
                        return true;
                    }
                }
            }

            offset += len;
        }

        info = new Info(true, recordEnd, null, null, null);
        return true;
    }

    public static int MidSldOffset(string host)
    {
        if (string.IsNullOrEmpty(host)) return 1;
        var parts = host.Split('.');
        if (parts.Length < 2)
        {
            return Math.Max(1, host.Length / 2);
        }

        var sld = parts[^2];
        if (string.IsNullOrEmpty(sld)) return Math.Max(1, host.Length / 2);
        var sldStart = host.LastIndexOf(sld + "." + parts[^1], StringComparison.Ordinal);
        if (sldStart < 0) sldStart = host.IndexOf(sld, StringComparison.Ordinal);
        if (sldStart < 0) sldStart = 0;
        return sldStart + Math.Max(1, sld.Length / 2);
    }

    public static int ResolveSplitPosition(ReadOnlySpan<byte> data, string splitAt, int fallbackPosition)
    {
        var max = Math.Max(1, data.Length - 1);
        var fallback = Math.Clamp(fallbackPosition, 1, max);
        if (!TryParse(data, out var info) || info.SniStart is not int sniStart || info.SniLength is not int sniLen || sniLen <= 0)
        {
            return fallback;
        }

        var pos = splitAt.ToLowerInvariant() switch
        {
            "sniext" => sniStart,
            "sni" => sniStart + 1,
            "midsld" => sniStart + MidSldOffset(info.SniHost ?? ""),
            _ => sniStart + Math.Max(1, sniLen / 2)
        };
        return Math.Clamp(pos, 1, max);
    }

    public static bool TryFragmentRecord(ReadOnlySpan<byte> record, int payloadSplit, out byte[] first, out byte[] second)
    {
        first = Array.Empty<byte>();
        second = Array.Empty<byte>();
        if (record.Length < 7 || record[0] != 0x16) return false;
        var payloadLen = (record[3] << 8) | record[4];
        if (record.Length < 5 + payloadLen) payloadLen = record.Length - 5;
        if (payloadLen < 2) return false;
        var split = Math.Clamp(payloadSplit, 1, payloadLen - 1);
        first = new byte[5 + split];
        first[0] = record[0];
        first[1] = record[1];
        first[2] = record[2];
        first[3] = (byte)(split >> 8);
        first[4] = (byte)split;
        record.Slice(5, split).CopyTo(first.AsSpan(5));

        var rest = payloadLen - split;
        second = new byte[5 + rest];
        second[0] = record[0];
        second[1] = record[1];
        second[2] = record[2];
        second[3] = (byte)(rest >> 8);
        second[4] = (byte)rest;
        record.Slice(5 + split, rest).CopyTo(second.AsSpan(5));
        return true;
    }
}
