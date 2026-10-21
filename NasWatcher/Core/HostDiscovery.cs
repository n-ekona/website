using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NasWatcher.Core;

/// <summary>スキャンで発見したホスト。</summary>
public sealed record DiscoveredHost(
    string Ip,
    string HostName,
    string Kind,
    IReadOnlyList<int> OpenPorts,
    string? ServerComment,
    double LatencyMs)
{
    /// <summary>GUI のリスト表示用。</summary>
    public string PortsText => OpenPorts.Count == 0 ? "-" : string.Join(", ", OpenPorts);

    public string NameText => string.IsNullOrWhiteSpace(HostName) ? Ip : $"{HostName} ({Ip})";
}

/// <summary>有線LAN 上の NAS を探索します。</summary>
public sealed partial class HostDiscovery
{
    private static readonly int[] ProbePorts = [445, 5000, 5001, 80, 443, 8080, 22];

    /// <summary>有線LAN サブネットをスキャンして NAS 候補を探します。</summary>
    public async Task<IReadOnlyList<DiscoveredHost>> ScanAsync(
        AdapterInfo adapter,
        IEnumerable<string> extraIps,
        int timeoutMs,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var subnet = adapter.Subnet;
        var candidates = new Dictionary<string, byte>(StringComparer.Ordinal);

        // 手動指定 IP
        foreach (var extra in extraIps)
        {
            if (IPAddress.TryParse(extra.Trim(), out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                candidates[parsed.ToString()] = 0;
            }
        }

        // ARP テーブル (同じサブネット内のホスト)
        if (subnet is not null)
        {
            foreach (var ip in ReadArpTable())
            {
                if (IsInSubnet(ip, subnet) && !candidates.ContainsKey(ip))
                {
                    candidates[ip] = 0;
                }
            }
        }

        // サブネット全体のスキャン (上限つき)
        if (subnet is not null)
        {
            var hostCount = 1 << (32 - subnet.PrefixLength);
            if (hostCount <= 1024)
            {
                foreach (var ip in subnet.EnumerateHosts(1024))
                {
                    if (!candidates.ContainsKey(ip.ToString()))
                    {
                        candidates[ip.ToString()] = 0;
                    }
                }
            }
            else
            {
                progress?.Report($"サブネットが大きいため /24 のみをスキャンします。");
                var narrow = adapter.IPv4 is null ? null : IpSubnet.FromAddress(adapter.IPv4, 24);
                if (narrow is not null)
                {
                    foreach (var ip in narrow.EnumerateHosts(256))
                    {
                        candidates.TryAdd(ip.ToString(), 0);
                    }
                }
            }
        }

        progress?.Report($"{candidates.Count} 個のアドレスを調査しています...");

        // (ホスト x ポート) をまとめて並列で調査する
        var tasks = candidates.Keys
            .Select(ip => ProbeHostAsync(ip, timeoutMs, cancellationToken))
            .ToList();

        var results = new List<DiscoveredHost>();
        foreach (var task in tasks)
        {
            var host = await task.ConfigureAwait(false);
            if (host is not null) results.Add(host);
        }

        // NAS 候補を優先して表示
        results.Sort((a, b) =>
        {
            var rank = Rank(b.Kind).CompareTo(Rank(a.Kind));
            return rank != 0 ? rank : a.Ip.CompareTo(b.Ip);
        });

        return results;
    }

    private static int Rank(string kind) => kind switch
    {
        "NAS (SMB)" => 3,
        "NAS / Web" => 2,
        "Web サーバー" => 1,
        _ => 0,
    };

    private static string Classify(IReadOnlyList<int> openPorts, ServerInfo? info)
    {
        var hasSmb = openPorts.Contains(445);
        var hasWeb = openPorts.Any(p => p is 80 or 443 or 5000 or 5001 or 8080);
        var hasDsm = openPorts.Any(p => p is 5000 or 5001);

        if (hasSmb && hasDsm) return "NAS / Web";
        if (hasSmb) return "NAS (SMB)";
        if (hasWeb) return "Web サーバー";
        if (openPorts.Contains(22)) return "SSH サーバー";
        return "不明";
    }

    private static readonly SemaphoreSlim PortGate = new(220);

    private async Task<DiscoveredHost?> ProbeHostAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var portTasks = ProbePorts.Select(async port =>
            {
                var connected = await TryConnectAsync(ip, port, timeoutMs, ct).ConfigureAwait(false);
                return connected ? port : -1;
            }).ToList();

            var portResults = await Task.WhenAll(portTasks).ConfigureAwait(false);
            sw.Stop();

            var openPorts = portResults.Where(p => p > 0).OrderBy(p => p).ToList();
            if (openPorts.Count == 0) return null;

            var info = SmbServerInfo.TryGet(ip);
            var hostName = await ResolveHostNameAsync(ip).ConfigureAwait(false);
            var kind = Classify(openPorts, info);
            return new DiscoveredHost(ip, hostName, kind, openPorts, info?.Comment, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ホスト {ip} の調査に失敗しました。", ex);
            return null;
        }
    }

    private static async Task<bool> TryConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        await PortGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            PortGate.Release();
        }
    }

    private static async Task<string> ResolveHostNameAsync(string ip)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).ConfigureAwait(false);
            return entry.HostName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsInSubnet(string ipText, IpSubnet subnet)
    {
        if (!IPAddress.TryParse(ipText, out var address)) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var net = subnet.Network.GetAddressBytes();
        var addr = address.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            var bits = Math.Clamp(subnet.PrefixLength - (i * 8), 0, 8);
            var mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
            if ((addr[i] & mask) != (net[i] & mask)) return false;
        }

        return true;
    }

    private static IEnumerable<string> ReadArpTable()
    {
        var results = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Default,
            };

            using var process = Process.Start(psi);
            if (process is null) return results;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            foreach (Match match in Ipv4Regex().Matches(output))
            {
                var text = match.Value;
                if (!results.Contains(text))
                {
                    results.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("arp テーブルの取得に失敗しました。", ex);
        }

        return results;
    }

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Ipv4Regex();
}
