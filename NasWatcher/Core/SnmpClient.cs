using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NasWatcher.Core;

/// <summary>SNMP (v1 / v2c) で取得したストレージ情報。</summary>
public sealed record SnmpCapacity(ulong TotalBytes, ulong UsedBytes, ulong FreeBytes, string Source)
{
    public double UsedRatio => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

/// <summary>
/// 最小限の SNMP GET クライアント。
/// NAS のストレージ容量を HOST-RESOURCES-MIB / UCD-SNMP-MIB から取得します。
/// </summary>
public sealed class SnmpClient
{
    private readonly string _community;
    private readonly int _port;
    private readonly int _timeoutMs;

    public SnmpClient(string? community = null, int port = 161, int timeoutMs = 2500)
    {
        _community = string.IsNullOrWhiteSpace(community) ? "public" : community.Trim();
        _port = port;
        _timeoutMs = timeoutMs;
    }

    //  Wilderness MIB 相当の代表 OID
    private static readonly string[] StorageOids =
    [
        "1.3.6.1.2.1.25.2.3.1.4",   // hrStorageAllocationUnits
        "1.3.6.1.2.1.25.2.3.1.5",   // hrStorageUsed
        "1.3.6.1.2.1.25.2.3.1.6",   // hrStorageSize
        "1.3.6.1.4.1.2021.11.9.1.1.6", // ucdavisTotalReal (KB)
        "1.3.6.1.4.1.2021.11.9.1.1.7", // ucdavisAvailReal (KB)
        "1.3.6.1.2.1.1.2",           // sysDescr
    ];

    /// <summary>指定 OID 群をまとめて取得します。</summary>
    public Dictionary<string, object?> Get(string ip, IReadOnlyList<string> oids)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (oids.Count == 0) return result;

        var requestId = Random.Shared.Next(1000, 60000);
        var packet = BuildGetRequest(oids, requestId);

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = _timeoutMs;
        udp.Client.SendTimeout = _timeoutMs;

        try
        {
            udp.Send(packet, new IPEndPoint(IPAddress.Parse(ip), _port));

            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            var response = udp.Receive(ref endPoint);
            ParseResponse(response, result);
        }
        catch (Exception ex)
        {
            AppLog.Info($"SNMP GET ({ip}) に失敗しました: {ex.Message}");
        }

        return result;
    }

    /// <summary>ストレージ容量を取得します。取得できない場合は null。</summary>
    public SnmpCapacity? TryGetCapacity(string ip)
    {
        var values = Get(ip, StorageOids);

        // --- HOST-RESOURCES-MIB ---
        if (values.TryGetValue("1.3.6.1.2.1.25.2.3.1.6", out var sizeObj)
            && values.TryGetValue("1.3.6.1.2.1.25.2.3.1.5", out var usedObj)
            && values.TryGetValue("1.3.6.1.2.1.25.2.3.1.4", out var unitObj))
        {
            var unit = ToUInt64(unitObj);
            var size = ToUInt64(sizeObj);
            var used = ToUInt64(usedObj);

            if (unit > 0 && size > 0)
            {
                var total = size * unit;
                var usedBytes = used * unit;
                return new SnmpCapacity(total, usedBytes, total > usedBytes ? total - usedBytes : 0, "SNMP (HOST-RESOURCES-MIB)");
            }
        }

        // --- UCD-SNMP-MIB (ucdavis) ---
        if (values.TryGetValue("1.3.6.1.4.1.2021.11.9.1.1.6", out var totalKbObj)
            && values.TryGetValue("1.3.6.1.4.1.2021.11.9.1.1.7", out var freeKbObj))
        {
            var totalKb = ToUInt64(totalKbObj);
            var freeKb = ToUInt64(freeKbObj);

            if (totalKb > 0)
            {
                var total = totalKb * 1024;
                var free = freeKb * 1024;
                var used = total > free ? total - free : 0;
                return new SnmpCapacity(total, used, free, "SNMP (UCD-SNMP-MIB)");
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ BER

    private byte[] BuildGetRequest(IReadOnlyList<string> oids, int requestId)
    {
        var varBinds = new List<byte>();

        foreach (var oid in oids)
        {
            var inner = new List<byte>();
            inner.AddRange(EncodeOid(oid));
            inner.Add(0x05); // NULL
            inner.Add(0x00);

            var bind = new List<byte> { 0x30 };
            bind.AddRange(EncodeLength(inner.Count));
            bind.AddRange(inner);

            varBinds.AddRange(bind);
        }

        var varBindList = new List<byte> { 0x30 };
        varBindList.AddRange(EncodeLength(varBinds.Count));
        varBindList.AddRange(varBinds);

        var pduBody = new List<byte>();
        pduBody.AddRange(EncodeInteger(requestId));
        pduBody.AddRange(EncodeInteger(0)); // error-status
        pduBody.AddRange(EncodeInteger(0)); // error-index
        pduBody.AddRange(varBindList);

        var pdu = new List<byte> { 0xA0 }; // GetRequest
        pdu.AddRange(EncodeLength(pduBody.Count));
        pdu.AddRange(pduBody);

        var message = new List<byte>();
        message.AddRange(EncodeInteger(1)); // version = v2c
        message.AddRange(EncodeOctetString(_community));
        message.AddRange(pdu);

        var result = new List<byte> { 0x30 };
        result.AddRange(EncodeLength(message.Count));
        result.AddRange(message);
        return result.ToArray();
    }

    private static void ParseResponse(byte[] data, Dictionary<string, object?> result)
    {
        var offset = 0;
        if (!ReadTlv(data, ref offset, out var tag, out var value)) return;
        if (tag != 0x30) return;

        // version
        if (!ReadTlv(data, ref offset, out tag, out value)) return;
        // community
        if (!ReadTlv(data, ref offset, out tag, out value)) return;
        // pdu
        if (!ReadTlv(data, ref offset, out tag, out value)) return;
        if (tag != 0xA2) return;

        var pdu = value;
        var p = 0;
        if (!ReadTlv(pdu, ref p, out tag, out value)) return; // request-id
        if (!ReadTlv(pdu, ref p, out tag, out value)) return; // error-status
        var errorStatus = ToInt64(value);
        if (!ReadTlv(pdu, ref p, out tag, out value)) return; // error-index
        if (!ReadTlv(pdu, ref p, out tag, out value)) return; // varbind list

        if (errorStatus != 0)
        {
            AppLog.Info($"SNMP 応答にエラーがあります (error-status={errorStatus})");
            return;
        }

        var list = value;
        var l = 0;
        while (l < list.Length)
        {
            if (!ReadTlv(list, ref l, out tag, out value)) break;
            if (tag != 0x30) continue;

            var bind = value;
            var b = 0;
            if (!ReadTlv(bind, ref b, out tag, out value)) continue;
            var oid = DecodeOid(value);

            if (!ReadTlv(bind, ref b, out tag, out value)) continue;
            result[oid] = DecodeValue(tag, value);
        }
    }

    private static object? DecodeValue(byte tag, byte[] value) => tag switch
    {
        0x02 => ToInt64(value),                      // INTEGER
        0x04 => Encoding.UTF8.GetString(value),      // OCTET STRING
        0x06 => DecodeOid(value),                     // OID
        0x40 => value.Length == 4 ? new IPAddress(value).ToString() : null, // IpAddress
        0x41 or 0x42 or 0x43 or 0x44 => ToUInt64(value), // Gauge32 / Unsigned32 / TimeTicks / Opaque
        0x46 => ToUInt64(value),                      // Counter64
        0x05 => null,                                // NULL
        _ => value.Length > 0 ? value[0] : null,
    };

    private static bool ReadTlv(byte[] data, ref int offset, out byte tag, out byte[] value)
    {
        tag = 0;
        value = Array.Empty<byte>();

        if (offset >= data.Length) return false;
        tag = data[offset++];

        if (offset >= data.Length) return false;
        int length = data[offset++];

        if ((length & 0x80) != 0)
        {
            var count = length & 0x7F;
            if (count == 0 || offset + count > data.Length) return false;
            length = 0;
            for (var i = 0; i < count; i++) length = (length << 8) | data[offset++];
        }

        if (offset + length > data.Length) length = data.Length - offset;
        value = new byte[length];
        Array.Copy(data, offset, value, 0, length);
        offset += length;
        return true;
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80) return [(byte)length];

        var bytes = new List<byte>();
        var v = length;
        while (v > 0)
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }

        var result = new List<byte> { (byte)(0x80 | bytes.Count) };
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] EncodeInteger(long value)
    {
        var bytes = new List<byte>();
        var v = value;
        var negative = v < 0;

        bytes.Add((byte)(v & 0xFF));
        v >>= 8;
        while (v != 0 && v != -1)
        {
            bytes.Add((byte)(v & 0xFF));
            v >>= 8;
        }

        if (negative) bytes[bytes.Count - 1] |= 0x80;
        if (bytes.Count == 1 && bytes[0] == 0) bytes[0] = 0;

        var result = new List<byte> { 0x02 };
        result.AddRange(EncodeLength(bytes.Count));
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] EncodeOctetString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new List<byte> { 0x04 };
        result.AddRange(EncodeLength(bytes.Length));
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] EncodeOid(string oid)
    {
        var parts = oid.Split('.').Select(int.Parse).ToArray();
        var bytes = new List<byte>();

        bytes.Add((byte)(parts[0] * 40 + parts[1]));
        for (var i = 2; i < parts.Length; i++)
        {
            var value = (ulong)parts[i];
            var stack = new List<byte> { (byte)(value & 0x7F) };
            value >>= 7;
            while (value > 0)
            {
                stack.Insert(0, (byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            bytes.AddRange(stack);
        }

        var result = new List<byte> { 0x06 };
        result.AddRange(EncodeLength(bytes.Count));
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static string DecodeOid(byte[] value)
    {
        if (value.Length == 0) return string.Empty;

        var parts = new List<string>
        {
            (value[0] / 40).ToString(),
            (value[0] % 40).ToString(),
        };

        var current = 0UL;
        for (var i = 1; i < value.Length; i++)
        {
            current = (current << 7) | (ulong)(value[i] & 0x7Fu);
            if ((value[i] & 0x80) == 0)
            {
                parts.Add(current.ToString());
                current = 0;
            }
        }

        return string.Join(".", parts);
    }

    private static long ToInt64(byte[] value)
    {
        long result = 0;
        foreach (var b in value) result = (result << 8) | (uint)b;
        return result;
    }

    private static ulong ToUInt64(object? value) => value switch
    {
        ulong u => u,
        long l when l >= 0 => (ulong)l,
        long l => 0,
        int i => (ulong)i,
        byte b => b,
        _ => 0,
    };
}
