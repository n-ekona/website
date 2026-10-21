using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NasWatcher.Core;

/// <summary>モバイルホットスポット経由での NAS アクセスを診断・許可します。</summary>
public sealed class LanAccessHelper
{
    /// <summary>接続テストの結果。</summary>
    public sealed record TestResult(bool Success, string Message, double ElapsedMs, string? Hint);

    /// <summary>ホットスポット (ICS) 用アダプター的信息。</summary>
    public sealed record HotspotAdapter(string Name, string Description, IPAddress Address, int PrefixLength)
    {
        /// <summary>クライアント端末が使うサブネット。</summary>
        public string Subnet => $"{Address}/24";

        /// <summary>接続先端末に設定するゲートウェイ。</summary>
        public string Gateway => Address.ToString();
    }

    /// <summary>アクティブな ICS (ホットスポット) アダプターを取得します。</summary>
    public HotspotAdapter? FindHotspotAdapter()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var description = ni.Description ?? "";
            if (!description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var address = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address))?.Address;

                if (address is null) continue;

                return new HotspotAdapter(ni.Name ?? description, description, address, 24);
            }
            catch
            {
                // 取得できないアダプターはスキップ
            }
        }

        return null;
    }

    /// <summary>既定ゲートウェイを持つ (インターネット接続用の) アダプターの IPv4 を取得します。</summary>
    public IPAddress? FindUplinkAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            try
            {
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address));
                if (!hasGateway) continue;

                var address = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address))?.Address;

                if (address is not null) return address;
            }
            catch
            {
                // 取得できないアダプターはスキップ
            }
        }

        return null;
    }

    /// <summary>
    /// ホットスポットのインターフェースを束縛して NAS へ接続し検証します。
    /// (iPhone と同じ経路でアクセスできるかを確認できます)
    /// </summary>
    public async Task<TestResult> TestConnectionAsync(string nasIp, int port, string? hotspotIp = null, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var bindIp = hotspotIp;
            if (string.IsNullOrEmpty(bindIp))
            {
                var adapter = FindHotspotAdapter();
                bindIp = adapter?.Address.ToString();
            }

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                SendTimeout = timeoutMs,
                ReceiveTimeout = timeoutMs,
            };

            if (!string.IsNullOrEmpty(bindIp) && IPAddress.TryParse(bindIp, out var bindAddress))
            {
                socket.Bind(new IPEndPoint(bindAddress, 0));
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            await socket.ConnectAsync(nasIp, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            return new TestResult(true, $"{nasIp}:{port} へ接続できました ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new TestResult(false, $"{nasIp}:{port} が応答しませんでした (タイムアウト)。", sw.ElapsedMilliseconds,
                "NAS の電源・IP アドレス・共有名を確認してください。");
        }
        catch (SocketException ex)
        {
            sw.Stop();
            var hint = ex.SocketErrorCode switch
            {
                SocketError.NotConnected => "ホットスポット経由の通信がファイアウォールで遮断されています。［LAN アクセスを許可］を実行してください。",
                SocketError.TimedOut => "応答がありません。NAS が正しい IP かどうか確認してください。",
                SocketError.ConnectionRefused => "接続は拒否されました。NAS 側の機能が停止しています。",
                SocketError.NetworkUnreachable => "この增加到 NAS への経路がありません。",
                _ => null,
            };

            return new TestResult(false, $"{nasIp}:{port} への接続に失敗しました: {ex.Message} (SocketError: {ex.SocketErrorCode})", sw.ElapsedMilliseconds, hint);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(false, $"{nasIp}:{port} への接続に失敗しました: {ex.Message}", sw.ElapsedMilliseconds, null);
        }
    }

    /// <summary>管理者権限で、モバイル端末から LAN への通信を許可します。</summary>
    public (bool Success, string Message) EnableLanAccess(string subnet)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
netsh advfirewall firewall delete rule name=""NasWatcher モバイル→LAN 許可"" | Out-Null
netsh advfirewall firewall add rule name=""NasWatcher モバイル→LAN 許可"" dir=in action=allow protocol=any remoteip={subnet} profile=any
netsh advfirewall firewall add rule name=""NasWatcher モバイル→LAN 許可 (out)"" dir=out action=allow protocol=any remoteip={subnet} profile=any
Get-NetConnectionProfile | Where-Object {{ $_.InterfaceAlias -like '*ローカル エリア*' -or $_.InterfaceAlias -like '*Local Area*' }} | ForEach-Object {{
    Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private -ErrorAction SilentlyContinue
}}
Get-NetIPInterface | Where-Object {{ $_.InterfaceAlias -like '*ローカル エリア*' -or $_.InterfaceAlias -like '*Local Area*' }} | ForEach-Object {{
    Enable-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
}}
Write-Output 'OK'
";

        return RunElevated(script);
    }

    /// <summary>管理者権限で、許可したルールを削除します。</summary>
    public (bool Success, string Message) DisableLanAccess()
    {
        var script = @"
$ErrorActionPreference = 'SilentlyContinue'
netsh advfirewall firewall delete rule name=""NasWatcher モバイル→LAN 許可""
netsh advfirewall firewall delete rule name=""NasWatcher モバイル→LAN 許可 (out)""
Write-Output 'OK'
";

        return RunElevated(script);
    }

    /// <summary>許可ルールが既にあるか確認します。</summary>
    public bool IsLanAccessEnabled()
    {
        try
        {
            var output = RunHidden("netsh", "advfirewall firewall show rule name=\"NasWatcher モバイル→LAN 許可\"");
            return output.Contains("NasWatcher", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static (bool, string) RunElevated(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process is null) return (false, "管理者権限のプロセスを起動できませんでした。");

            process.WaitForExit(60_000);
            return process.ExitCode == 0
                ? (true, "適用しました。")
                : (false, $"適用に失敗しました (終了コード {process.ExitCode})。");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "管理者権限が許可されませんでした。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string RunHidden(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return string.Empty;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return output;
    }
}
