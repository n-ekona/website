using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NasWatcher.Core;

/// <summary>ネットワークアダプターの情報。</summary>
public sealed class AdapterInfo
{
    public required string Id { get; init; }

    /// <summary>表示名 (Windows での网卡名)。</summary>
    public required string Name { get; init; }

    /// <summary>アダプターの説明。</summary>
    public required string Description { get; init; }

    public required NetworkInterfaceType Type { get; init; }

    public bool IsUp { get; init; }

    public bool IsLoopback { get; init; }

    /// <summary>現在割り当てられている IPv4 アドレス。</summary>
    public IPAddress? IPv4 { get; init; }

    /// <summary>サブネットプレフィックス長。</summary>
    public int PrefixLength { get; init; }

    public IPAddress? Gateway { get; init; }

    public string? MacAddress { get; init; }

    public string? DhcpServer { get; init; }

    /// <summary>モバイルホットスポット (Wi-Fi Direct) 用の仮想アダプターかどうか。</summary>
    public bool IsHotspotAdapter { get; init; }

    /// <summary>仮想化ソフトウェア / トンネリング由来で物理有線LANではないと判断したもの。</summary>
    public bool IsVirtual { get; init; }

    public string IpText => IPv4?.ToString() ?? "(未接続)";

    public string LinkText => IsUp ? "接続中" : "切断";

    public override string ToString() => $"{Name}  ({IpText})";

    /// <summary>このアダプターの所属サブネット。</summary>
    public IpSubnet? Subnet => IPv4 is null || PrefixLength <= 0 ? null : IpSubnet.FromAddress(IPv4, PrefixLength);
}

/// <summary>IPv4 のネットワーク (アドレス + プレフィックス)。</summary>
public sealed class IpSubnet
{
    public required IPAddress Network { get; init; }

    public required int PrefixLength { get; init; }

    public static IpSubnet? FromAddress(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        if (prefixLength is < 0 or > 32) return null;

        var bytes = address.GetAddressBytes();
        var mask = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var bits = Math.Clamp(prefixLength - (i * 8), 0, 8);
            mask[i] = bits == 0 ? (byte)0 : (byte)(0xFF << (8 - bits));
        }

        var networkBytes = new byte[4];
        for (var i = 0; i < 4; i++) networkBytes[i] = (byte)(bytes[i] & mask[i]);

        return new IpSubnet { Network = new IPAddress(networkBytes), PrefixLength = prefixLength };
    }

    /// <summary>このサブネットに含まれる IP を列挙します (上限あり)。</summary>
    public IEnumerable<IPAddress> EnumerateHosts(int maxHosts)
    {
        var net = Network.GetAddressBytes();
        var count = 1 << (32 - PrefixLength);
        // ネットワークアドレスとブロードキャストを除く
        var start = PrefixLength >= 31 ? 0 : 1;
        var end = PrefixLength >= 31 ? count - 1 : count - 2;

        var emitted = 0;
        for (var i = start; i <= end && emitted < maxHosts; i++)
        {
            var bytes = new byte[4];
            var value = i;
            for (var b = 3; b >= 0; b--)
            {
                bytes[b] = (byte)((net[b] + value) & 0xFF);
                value >>= 8;
            }

            emitted++;
            yield return new IPAddress(bytes);
        }
    }

    public override string ToString() => $"{Network}/{PrefixLength}";
}

/// <summary>ローカルアダプターの列挙。</summary>
public static class NetworkAdapters
{
    private static readonly string[] VirtualHints =
    [
        "virtualbox", "vmware", "hyper-v", "virtual machine", "tap-windows", "tap adapter",
        "tunnel", "loopback", "bluetooth", "teredo", "6to4", "ip-https", "sstp", "ikev2", "l2tp", "pptp",
        "pseudo", "vpn", "nordlynx", "wireguard", "tailscale", "zerotier", "npcap", "wintun", "radmin",
    ];

    /// <summary>すべてのアダプターの情報を取得します。</summary>
    public static List<AdapterInfo> GetAll()
    {
        var list = new List<AdapterInfo>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            AppLog.Warn("ネットワークアダプターの列挙に失敗しました。", ex);
            return list;
        }

        foreach (var ni in interfaces)
        {
            var description = ni.Description ?? "";
            var name = ni.Name ?? "";
            var isUp = ni.OperationalStatus == OperationalStatus.Up;
            var isLoopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;

            IPAddress? ip = null;
            int prefix = -1;
            IPAddress? gateway = null;
            try
            {
                var props = ni.GetIPProperties();
                var unicast = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address));
                if (unicast is not null)
                {
                    ip = unicast.Address;
                    prefix = PrefixFromMask(unicast.IPv4Mask);
                }

                gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            }
            catch
            {
                // 一部のアダプターはプロパティ取得で失敗する
            }

            var lower = (name + " " + description).ToLowerInvariant();
            var isVirtual = VirtualHints.Any(h => lower.Contains(h));
            var isHotspot = lower.Contains("wi-fi direct virtual adapter");

            list.Add(new AdapterInfo
            {
                Id = ni.Id,
                Name = name,
                Description = description,
                Type = ni.NetworkInterfaceType,
                IsUp = isUp && !isLoopback,
                IsLoopback = isLoopback,
                IPv4 = ip,
                PrefixLength = prefix,
                Gateway = gateway,
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                IsVirtual = isVirtual,
                IsHotspotAdapter = isHotspot,
            });
        }

        return list;
    }

    /// <summary>有線LAN (Ethernet) の稼働中アダプターを返します。</summary>
    public static List<AdapterInfo> GetEthernetAdapters()
        => GetAll()
            .Where(a => a.Type == NetworkInterfaceType.Ethernet
                        && !a.IsLoopback
                        && !a.IsVirtual)
            .OrderByDescending(a => a.IsUp)
            .ThenByDescending(a => a.IPv4 is not null)
            .ThenBy(a => a.Name)
            .ToList();

    /// <summary>:id に一致するアダプターを返します。</summary>
    public static AdapterInfo? Find(string? id)
        => id is null ? null : GetAll().FirstOrDefault(a => a.Id == id);

    private static int PrefixFromMask(IPAddress? mask)
    {
        if (mask is null) return -1;
        var bytes = mask.GetAddressBytes();
        if (bytes.Length != 4) return -1;

        var prefix = 0;
        var partial = false;

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];

            if (b == 0xFF)
            {
                if (partial) return -1;
                prefix += 8;
                continue;
            }

            // 先頭から連続する 1 ビットを数える
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0)
                {
                    prefix++;
                    partial = true;
                }
                else
                {
                    break;
                }
            }

            // マスクは連続していなければならない (後ろのバイトはすべて 0)
            for (var j = i + 1; j < bytes.Length; j++)
            {
                if (bytes[j] != 0) return -1;
            }

            break;
        }

        return prefix;
    }

    private static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
