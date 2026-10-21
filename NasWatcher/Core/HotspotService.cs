using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace NasWatcher.Core;

/// <summary>モバイルホットスポットの状態。</summary>
public sealed record HotspotStatus(
    string StateText,
    bool IsOn,
    string Ssid,
    string Passphrase,
    string Band,
    int ClientCount,
    int MaxClientCount,
    IReadOnlyList<string> ClientAddresses,
    string? GatewayIp,
    string? Message)
{
    public string ClientsText => ClientCount == 0
        ? "接続中のクライアントはありません"
        : string.Join(", ", ClientAddresses);
}

/// <summary>Windows のモバイルホットスポット (テザリング) を制御します。</summary>
public sealed class HotspotService
{
    private const int OperationTimeoutSeconds = 45;

    /// <summary>現在の状態を取得します。</summary>
    public async Task<HotspotStatus> GetStatusAsync()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return new HotspotStatus("利用不可", false, "-", "-", "-", 0, 0, [], null,
                    "インターネットに接続していません。モバイルホットスポットは上り接続が必要です。");
            }

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            var state = manager.TetheringOperationalState;

            string ssid = "-";
            string passphrase = "-";
            string band = "-";
            try
            {
                var config = manager.GetCurrentAccessPointConfiguration();
                ssid = string.IsNullOrEmpty(config.Ssid) ? "-" : config.Ssid;
                passphrase = string.IsNullOrEmpty(config.Passphrase) ? "-" : config.Passphrase;
                band = config.Band.ToString();
            }
            catch
            {
                // 停止中は設定取得に失敗することがある
            }

            var clients = new List<string>();
            try
            {
                foreach (var client in manager.GetTetheringClients())
                {
                    var ip = client.HostNames.FirstOrDefault()?.CanonicalName;
                    var mac = client.MacAddress;

                    // 名前解決できない場合は MAC のみを表示する
                    if (!System.Net.IPAddress.TryParse(ip, out _))
                    {
                        ip = null;
                    }

                    clients.Add(ip is null ? mac : $"{ip} ({mac})");
                }
            }
            catch
            {
                // クライアント取得は失敗しても致命的ではない
            }

            var gateway = FindHotspotAdapterAddress();

            var message = state == TetheringOperationalState.On
                ? null
                : "オフです。［オン］ボタンで開始できます。";

            return new HotspotStatus(
                DescribeState(state),
                state == TetheringOperationalState.On,
                ssid,
                passphrase,
                band,
                (int)manager.ClientCount,
                (int)manager.MaxClientCount,
                clients,
                gateway,
                message);
        }
        catch (Exception ex)
        {
            AppLog.Error("ホットスポット状態の取得に失敗しました。", ex);
            return new HotspotStatus("エラー", false, "-", "-", "-", 0, 0, [], null, ex.Message);
        }
    }

    /// <summary>モバイルホットスポットを ON にします。</summary>
    public async Task<HotspotStatus> StartAsync()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) throw new InvalidOperationException("インターネット接続がありません。");

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);

            var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            if (capability != TetheringCapability.Enabled)
            {
                throw new InvalidOperationException(
                    $"この Wi-Fi アダプターはモバイルホットスポットに対応していません (状態: {capability})。");
            }

            AppLog.Info("モバイルホットスポットを ON にしています...");
            var operation = manager.StartTetheringAsync();
            var result = await AwaitWithTimeoutAsync(operation, OperationTimeoutSeconds).ConfigureAwait(false);
            if (result.Status != TetheringOperationStatus.Success)
            {
                throw new InvalidOperationException($"ホットスポットを開始できませんでした (Status: {result.Status})。");
            }

            AppLog.Info("モバイルホットスポットを ON にしました。");
            return await GetStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("モバイルホットスポットの ON に失敗しました。", ex);
            throw;
        }
    }

    /// <summary>モバイルホットスポットを OFF にします。</summary>
    public async Task<HotspotStatus> StopAsync()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) throw new InvalidOperationException("インターネット接続がありません。");

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState != TetheringOperationalState.On)
            {
                return await GetStatusAsync().ConfigureAwait(false);
            }

            AppLog.Info("モバイルホットスポットを OFF にしています...");
            var operation = manager.StopTetheringAsync();
            var result = await AwaitWithTimeoutAsync(operation, OperationTimeoutSeconds).ConfigureAwait(false);
            if (result.Status != TetheringOperationStatus.Success)
            {
                throw new InvalidOperationException($"ホットスポットを停止できませんでした (Status: {result.Status})。");
            }

            AppLog.Info("モバイルホットスポットを OFF にしました。");
            return await GetStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("モバイルホットスポットの OFF に失敗しました。", ex);
            throw;
        }
    }

    /// <summary>SSID とパスフレーズを変更します。</summary>
    public async Task<HotspotStatus> ConfigureAsync(string ssid, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(ssid)) throw new ArgumentException("SSID を入力してください。");
        if (!string.IsNullOrEmpty(passphrase) && passphrase.Length < 8)
            throw new ArgumentException("パスフレーズは 8 文字以上で入力してください。");

        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) throw new InvalidOperationException("インターネット接続がありません。");

        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        var config = manager.GetCurrentAccessPointConfiguration();
        config.Ssid = ssid;
        if (!string.IsNullOrEmpty(passphrase)) config.Passphrase = passphrase;

        await AwaitOperationAsync(manager.ConfigureAccessPointAsync(config)).ConfigureAwait(false);
        AppLog.Info($"ホットスポットの設定を更新しました (SSID: {ssid})。");
        return await GetStatusAsync().ConfigureAwait(false);
    }

    /// <summary>WinRT の非同期操作を Task に変換して待機します。</summary>
    private static async Task AwaitOperationAsync(Windows.Foundation.IAsyncAction action)
        => await action;

    private static async Task<T> AwaitOperationAsync<T>(Windows.Foundation.IAsyncOperation<T> operation)
        => await operation;

    private static async Task<NetworkOperatorTetheringOperationResult> AwaitWithTimeoutAsync(
        Windows.Foundation.IAsyncOperation<NetworkOperatorTetheringOperationResult> operation,
        int timeoutSeconds)
    {
        var task = AwaitOperationAsync(operation);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"操作が {timeoutSeconds} 秒以内に完了しませんでした。");
        }

        return await task.ConfigureAwait(false);
    }

    private static string DescribeState(TetheringOperationalState state) => state switch
    {
        TetheringOperationalState.On => "オン",
        TetheringOperationalState.Off => "オフ",
        TetheringOperationalState.InTransition => "切り替え中",
        TetheringOperationalState.Unknown => "不明",
        _ => state.ToString(),
    };

    private static string? FindHotspotAdapterAddress()
    {
        foreach (var adapter in NetworkAdapters.GetAll())
        {
            if (adapter.IsHotspotAdapter && adapter.IsUp && adapter.IPv4 is not null)
            {
                return adapter.IPv4.ToString();
            }
        }

        return null;
    }
}
