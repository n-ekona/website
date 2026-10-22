using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NasWatcher.Core;

namespace NasWatcher.UI;

/// <summary>HTML (WebView2) からの要求を処理し、状態を pushed するブリッジ。</summary>
internal sealed class AppBridge : IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SmbClient _smb = new();
    private readonly HotspotService _hotspot = new();
    private readonly HostDiscovery _discovery = new();
    private readonly SpeedMonitor _speed = new(TimeSpan.FromSeconds(1));
    private readonly LanAccessHelper _lan = new();
    private readonly TcpRelay _relay = new();
    private readonly HttpProxy _proxy = new();

    private AdapterInfo? _adapter;
    private DiscoveredHost? _host;
    private List<DiscoveredHost> _lastHosts = [];
    private CancellationTokenSource? _scanCts;
    private string? _speedAdapterId;
    private bool _hotspotBusy;

    /// <summary>UI へ push するイベント (event 名, データ)。</summary>
    public event Action<string, JsonNode>? Push;

    public AppBridge()
    {
        _speed.Updated += OnSpeedUpdated;
        _hotspotTimer = new System.Threading.Timer(async _ => await PushHotspotAsync().ConfigureAwait(false), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    private readonly System.Threading.Timer _hotspotTimer;

    private async Task PushHotspotAsync()
    {
        if (_hotspotBusy) return;

        try
        {
            var status = await _hotspot.GetStatusAsync().ConfigureAwait(false);
            PushEvent("hotspot", BuildHotspotNode(status));
        }
        catch (Exception ex)
        {
            AppLog.Warn("ホットスポット状態の push に失敗しました。", ex);
        }
    }

    // -------------------------------------------------------------- 要求処理

    public async Task<JsonNode?> HandleAsync(string action, JsonElement payload)
    {
        switch (action)
        {
            case "loadAdapters": return await LoadAdaptersAsync();
            case "selectAdapter": return SelectAdapter(GetString(payload, "id"));
            case "scan": return await ScanAsync(GetString(payload, "extraIp"));
            case "cancel": _scanCts?.Cancel(); return null;
            case "selectHost": return SelectHost(GetString(payload, "ip"));
            case "listShares": return await ListSharesAsync();
            case "getCapacity": return await GetCapacityAsync(GetString(payload, "share"));
            case "copy": CopyToClipboard(GetString(payload, "text")); return null;
            case "openSmb": OpenSmb(); return null;
            case "openWeb": OpenWeb(); return null;
            case "hotspot": return await ToggleHotspotAsync(GetBoolean(payload, "on"));
            case "getSettings": return BuildSettings();
            case "saveSettings": await SaveSettingsAsync(payload); return null;
            case "openLog": OpenLogFile(); return null;
            case "selectSpeedAdapter": _speedAdapterId = GetString(payload, "id"); return null;
            case "lanInfo": return BuildLanInfo();
            case "lanTest": return await TestLanAsync(GetString(payload, "ip"), GetInt(payload, "port", 445));
            case "lanEnable": return EnableLanAccess();
            case "lanDisable": return DisableLanAccess();
            case "relayStart": return StartRelay(GetInt(payload, "targetPort", 80));
            case "relayStop": return StopRelay();
            case "relayStatus": return BuildRelayInfo();
            default:
                throw new InvalidOperationException("未知のコマンド: " + action);
        }
    }

    // -------------------------------------------------------------- TCP リレー

    private JsonNode StartRelay(int targetPort)
    {
        var adapter = _lan.FindHotspotAdapter();
        var nasIp = _host?.Ip ?? _settings.NasIp;
        if (adapter is null) throw new InvalidOperationException("モバイルホットスポットが ON ではありません。");
        if (string.IsNullOrEmpty(nasIp)) throw new InvalidOperationException("NAS が選択されていません。");

        // 待ち受けポートは「転送先と同じポート」を優先し、空いていなければ自動で選ぶ
        // (iPhone のブラウザはポート指定ができるため、 NAS 側と同一のポートが扱いやすい)
        var candidates = new List<int> { targetPort };
        candidates.AddRange(new[] { 8080, 9000, 6000, 5050, 7000, 4500, 4440, 4450 });
        candidates = candidates.Distinct().ToList();

        // SMB (445) の場合、Windows 側の SMB サーバーが 445 を使っているため一時停止する
        if (targetPort == 445 && _lan.IsLocalSmbServerRunning())
        {
            var (stopped, message) = _lan.StopLocalSmbServer();
            if (stopped)
            {
                _smbServerStopped = true;
                AppLog.Info($"リレーのため Windows の SMB サーバーを一時停止しました: {message}");
            }
        }

        string? lastError = null;
        var started = false;

        foreach (var port in candidates)
        {
            try
            {
                if (IsHttpTarget(targetPort))
                {
                    _relay.Stop();
                    _proxy.Start(adapter.Address.ToString(), port, nasIp, targetPort);
                }
                else
                {
                    _proxy.Stop();
                    _relay.Start(adapter.Address.ToString(), port, nasIp, targetPort);
                }

                started = true;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        var info = BuildRelayInfo();

        if (!started)
        {
            throw new InvalidOperationException($"リレーの待受ポートを確保できませんでした: {lastError}");
        }

        if (!_lan.IsLanAccessEnabled())
        {
            info["hint"] = "iPhone からの接続をブロックしている可能性があります。［LAN アクセスを許可］を押してファイアウォール ルールを追加してください (管理者権限が必要です)。";
        }

        if (targetPort == 445 && info["port"]?.GetValue<int>() != 445)
        {
            info["hint"] = (info["hint"]?.GetValue<string>() ?? "")
                + " ※ iPhone の Files アプリは SMB のポートが 445 固定のため、"
                + "445 を使わない場合は WebDAV (5005) をご利用ください。";
        }

        return info;
    }

    /// <summary>HTTP として中継する対象ポートかどうか。</summary>
    private static bool IsHttpTarget(int port) => port is 80 or 443 or 5000 or 5001 or 5005 or 5006;

    private bool _smbServerStopped;

    private JsonNode StopRelay()
    {
        _relay.Stop();
        _proxy.Stop();

        // リレーのために停止していた Windows の SMB サーバーを戻す
        if (_smbServerStopped)
        {
            _smbServerStopped = false;
            _lan.StartLocalSmbServer();
        }

        return BuildRelayInfo();
    }

    private JsonNode BuildRelayInfo()
    {
        var relayStatus = _relay.GetStatus();
        var proxyStatus = _proxy.GetStatus();
        var running = relayStatus.Running || proxyStatus.Running;

        var adapter = _lan.FindHotspotAdapter();
        var nasIp = _host?.Ip ?? _settings.NasIp;
        var port = proxyStatus.Running ? proxyStatus.ListenPort : relayStatus.ListenPort;
        var targetPort = proxyStatus.Running ? proxyStatus.TargetPort : relayStatus.TargetPort;

        return new JsonObject
        {
            ["running"] = running,
            ["port"] = port,
            ["connections"] = relayStatus.ActiveConnections + proxyStatus.ActiveConnections,
            ["error"] = relayStatus.LastError ?? proxyStatus.LastError,
            ["gateway"] = adapter?.Gateway,
            ["nasIp"] = nasIp,
            ["targetPort"] = targetPort,
            ["url"] = running && adapter is not null
                ? $"http://{adapter.Address}:{port}"
                : null,
            ["smbTarget"] = running && adapter is not null && nasIp is not null
                ? $"smb://{adapter.Address}:{port}"
                : null,
            ["hint"] = null,
        };
    }

    // -------------------------------------------------------------- LAN アクセス

    private JsonNode BuildLanInfo()
    {
        var adapter = _lan.FindHotspotAdapter();
        var nasIp = _host?.Ip ?? _settings.NasIp;
        var port = WebPort;

        var webUrl = nasIp is null
            ? null
            : IsDefaultWebPort(port)
                ? (port == 443 ? $"https://{nasIp}" : $"http://{nasIp}")
                : $"http://{nasIp}:{port}";

        return new JsonObject
        {
            ["gateway"] = adapter?.Gateway,
            ["subnet"] = adapter?.Subnet,
            ["adapterName"] = adapter?.Name,
            ["enabled"] = _lan.IsLanAccessEnabled(),
            ["nasIp"] = nasIp,
            ["webUrl"] = webUrl,
            ["smbUrl"] = nasIp is null ? null : $"smb://{nasIp}/",
        };
    }

    private async Task<JsonNode> TestLanAsync(string? nasIp, int port)
    {
        if (string.IsNullOrEmpty(nasIp)) throw new InvalidOperationException("NAS が選択されていません。");

        var adapter = _lan.FindHotspotAdapter();

        // ICS は送信元を上位接続 (インターネット用) のアドレスに NAT するため、
        // モバイル端末と同じ経路を再現するにはそのアドレスを束縛する
        var sourceIp = _lan.FindUplinkAddress()?.ToString() ?? adapter?.Address.ToString();

        var result = await _lan.TestConnectionAsync(nasIp, port, sourceIp).ConfigureAwait(false);

        return new JsonObject
        {
            ["ok"] = result.Success,
            ["message"] = result.Message,
            ["hint"] = result.Hint,
            ["gateway"] = adapter?.Gateway,
            ["source"] = sourceIp,
        };
    }

    private JsonNode EnableLanAccess()
    {
        var adapter = _lan.FindHotspotAdapter();
        var subnet = adapter?.Subnet ?? "192.168.137.0/24";
        var (success, message) = _lan.EnableLanAccess(subnet);
        return new JsonObject { ["ok"] = success, ["message"] = message };
    }

    private JsonNode DisableLanAccess()
    {
        var (success, message) = _lan.DisableLanAccess();
        return new JsonObject { ["ok"] = success, ["message"] = message };
    }

    // -------------------------------------------------------------- アダプター

    private async Task<JsonNode> LoadAdaptersAsync()
    {
        var adapters = NetworkAdapters.GetEthernetAdapters();
        if (_adapter is not null)
        {
            var found = adapters.FirstOrDefault(a => a.Id == _adapter.Id);
            _adapter = found ?? (adapters.Count > 0 ? adapters[0] : null);
        }
        else
        {
            _adapter = adapters.FirstOrDefault();
        }

        var array = new JsonArray();
        foreach (var a in adapters)
        {
            array.Add(new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["description"] = a.Description,
                ["ip"] = a.IPv4?.ToString(),
                ["gateway"] = a.Gateway?.ToString(),
                ["up"] = a.IsUp,
            });
        }

        var hotspot = await _hotspot.GetStatusAsync().ConfigureAwait(false);

        return new JsonObject
        {
            ["adapters"] = array,
            ["selectedId"] = _adapter?.Id,
            ["info"] = DescribeAdapter(_adapter),
            ["nasIp"] = _host?.Ip ?? _settings.NasIp,
            ["hotspot"] = BuildHotspotNode(hotspot),
        };
    }

    private static string? DescribeAdapter(AdapterInfo? adapter)
    {
        if (adapter is null) return "有線LANアダプターが見つかりません。";
        return $"{adapter.Description}  /  IP: {adapter.IPv4?.ToString() ?? "未接続"}  /  "
             + $"ゲートウェイ: {adapter.Gateway?.ToString() ?? "なし"}  /  {adapter.LinkText}";
    }

    private JsonNode? SelectAdapter(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var adapter = NetworkAdapters.GetEthernetAdapters().FirstOrDefault(a => a.Id == id);
        _adapter = adapter;
        return new JsonObject { ["info"] = DescribeAdapter(adapter) };
    }

    // -------------------------------------------------------------- スキャン

    private async Task<JsonNode?> ScanAsync(string? extraIp)
    {
        if (_adapter is null) throw new InvalidOperationException("有線LANアダプターを選択してください。");
        if (_adapter.IPv4 is null) throw new InvalidOperationException("アダプターに IP アドレスが割り当てられていません。");

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        var extras = (extraIp ?? string.Empty)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var progress = new Progress<string>(text => PushEvent("scanStatus", new JsonObject
        {
            ["text"] = text,
            ["state"] = "busy",
        }));

        var hosts = await _discovery.ScanAsync(_adapter, extras, _settings.ProbeTimeoutMs, progress, token).ConfigureAwait(false);
        _lastHosts = hosts.ToList();

        var array = new JsonArray();
        foreach (var h in hosts)
        {
            array.Add(new JsonObject
            {
                ["ip"] = h.Ip,
                ["hostName"] = h.HostName,
                ["kind"] = h.Kind,
                ["ports"] = new JsonArray(h.OpenPorts.Select(p => (JsonNode)p).ToArray()),
                ["latency"] = h.LatencyMs,
                ["comment"] = h.ServerComment,
            });
        }

        PushEvent("scanStatus", new JsonObject
        {
            ["text"] = token.IsCancellationRequested
                ? "スキャンを中止しました。"
                : hosts.Count == 0 ? "NAS 候補は見つかりませんでした。" : $"{hosts.Count} 件のホストを検出しました。",
            ["state"] = token.IsCancellationRequested ? "" : hosts.Count == 0 ? "error" : "ok",
        });

        PushEvent("scanHosts", array);
        return null;
    }

    private JsonNode? SelectHost(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        _host = _lastHosts.FirstOrDefault(h => h.Ip == ip);
        _settings.NasIp = ip;

        // 検出したポートから Web 管理画面のポートを推測する
        if (_host is not null)
        {
            foreach (var port in new[] { 80, 443, 5000, 5001, 8080 })
            {
                if (_host.OpenPorts.Contains(port))
                {
                    _detectedWebPort = port;
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>NAS の Web アクセスに使うポート (検出した値があればそれを優先)。</summary>
    private int WebPort => _detectedWebPort ?? _settings.WebPort;

    /// <summary>ポートが省略できる既定ポートかどうか。</summary>
    private static bool IsDefaultWebPort(int port) => port is 80 or 443;

    private int? _detectedWebPort;

    // -------------------------------------------------------------- 共有 / 容量

    private async Task<JsonNode> ListSharesAsync()
    {
        var ip = _host?.Ip ?? _settings.NasIp;
        if (string.IsNullOrEmpty(ip))
        {
            return new JsonObject
            {
                ["shares"] = new JsonArray(),
                ["selected"] = null,
                ["error"] = "NAS が選択されていません。",
            };
        }

        var credentials = SmbClient.Credentials.FromSettings(_settings);
        var shares = await Task.Run(() => _smb.ListShares(ip, credentials)).ConfigureAwait(false);

        var array = new JsonArray(shares.Select(s => (JsonNode)s).ToArray());
        var selected = shares.FirstOrDefault(s => string.Equals(s, _settings.ShareName, StringComparison.OrdinalIgnoreCase))
                       ?? shares.FirstOrDefault();

        if (selected is not null) _settings.ShareName = selected;

        var error = shares.Count == 0
            ? "共有を取得できませんでした。設定タブの認証方式を確認してください。"
            : null;

        return new JsonObject { ["shares"] = array, ["selected"] = selected, ["error"] = error };
    }

    private async Task<JsonNode?> GetCapacityAsync(string? share)
    {
        var ip = _host?.Ip ?? _settings.NasIp;
        if (string.IsNullOrEmpty(ip)) return null;

        // 1) SMB で取得する
        if (!string.IsNullOrEmpty(share))
        {
            var credentials = SmbClient.Credentials.FromSettings(_settings);
            var result = await Task.Run(() => _smb.GetCapacity(ip, share, credentials)).ConfigureAwait(false);

            if (result.Success && result.Capacity is not null)
            {
                var capacity = result.Capacity;
                var percent = (int)Math.Round(capacity.UsedRatio * 100);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["method"] = "SMB",
                    ["percent"] = percent,
                    ["used"] = Format.Bytes(capacity.UsedBytes),
                    ["total"] = Format.Bytes(capacity.TotalBytes),
                    ["free"] = Format.Bytes(capacity.TotalFreeBytes),
                    ["auth"] = result.AuthLabel,
                    ["detail"] = $"{ip}\\{share}   使用率 {percent}%   空き {Format.Bytes(capacity.TotalFreeBytes)}"
                               + (result.AuthLabel is null ? string.Empty : $"   認証: {result.AuthLabel}"),
                };
            }

            AppLog.Info($"SMB での容量取得に失敗しました: {result.Message}");
            smbError = result.Message;
        }

        // 2) SMB で取得できない場合は SNMP で取得する
        if (_settings.UseSnmp)
        {
            var snmp = new SnmpClient(_settings.SnmpCommunity, _settings.SnmpPort);
            var snmpCapacity = await Task.Run(() => snmp.TryGetCapacity(ip)).ConfigureAwait(false);

            if (snmpCapacity is not null)
            {
                var percent = (int)Math.Round(snmpCapacity.UsedRatio * 100);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["method"] = "SNMP",
                    ["percent"] = percent,
                    ["used"] = Format.Bytes(snmpCapacity.UsedBytes),
                    ["total"] = Format.Bytes(snmpCapacity.TotalBytes),
                    ["free"] = Format.Bytes(snmpCapacity.FreeBytes),
                    ["auth"] = null,
                    ["detail"] = $"{ip}   使用率 {percent}%   空き {Format.Bytes(snmpCapacity.FreeBytes)}   取得方法: {snmpCapacity.Source}",
                };
            }
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = smbError ?? "容量を取得できませんでした。",
            ["hint"] = "SMB 認証の確認、または設定タブで SNMP コミュニティ文字列 (既定: public) を指定してください。",
        };
    }

    private string? smbError;

    // -------------------------------------------------------------- 操作

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            System.Windows.Forms.Clipboard.SetText(text);
            _settings.LastSmbPath = text;
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Error("クリップボードへのコピーに失敗しました。", ex);
            throw;
        }
    }

    private void OpenSmb()
    {
        var ip = _host?.Ip ?? _settings.NasIp;
        if (string.IsNullOrEmpty(ip)) return;
        var path = $@"\\{ip}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        AppLog.Info($"エクスプローラーで {path} を開きました。");
    }

    private void OpenWeb()
    {
        var ip = _host?.Ip ?? _settings.NasIp;
        if (string.IsNullOrEmpty(ip)) return;

        var port = WebPort;
        var url = IsDefaultWebPort(port)
            ? (port == 443 ? $"https://{ip}" : $"http://{ip}")
            : $"http://{ip}:{port}";

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        AppLog.Info($"{url} をブラウザーで開きました。");
    }

    private void OpenLogFile()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NasWatcher",
            "naswatcher.log");

        if (!File.Exists(path)) throw new FileNotFoundException("ログファイルはまだありません。", path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    // -------------------------------------------------------------- ホットスポット

    private async Task<JsonNode> ToggleHotspotAsync(bool turnOn)
    {
        _hotspotBusy = true;
        try
        {
            var status = turnOn
                ? await _hotspot.StartAsync().ConfigureAwait(false)
                : await _hotspot.StopAsync().ConfigureAwait(false);

            _hotspotBusy = false;
            return BuildHotspotNode(status);
        }
        finally
        {
            _hotspotBusy = false;
        }
    }

    private JsonObject BuildHotspotNode(HotspotStatus status) => new()
    {
        ["on"] = status.IsOn,
        ["stateText"] = status.StateText,
        ["ssid"] = status.Ssid,
        ["passphrase"] = status.Passphrase,
        ["band"] = status.Band,
        ["clientCount"] = status.ClientCount,
        ["maxClientCount"] = status.MaxClientCount,
        ["clientsText"] = status.ClientsText,
        ["gatewayIp"] = status.GatewayIp,
        ["message"] = status.Message,
        ["busy"] = _hotspotBusy,
    };

    // -------------------------------------------------------------- 設定

    private JsonObject BuildSettings() => new()
    {
        ["nasIp"] = _settings.NasIp,
        ["shareName"] = _settings.ShareName,
        ["webPort"] = _settings.WebPort,
        ["authMode"] = _settings.AuthMode,
        ["username"] = _settings.Username,
        ["password"] = _settings.Password,
        ["saveCredentials"] = _settings.SaveCredentials,
        ["autoScanOnStart"] = _settings.AutoScanOnStart,
        ["probeTimeoutMs"] = _settings.ProbeTimeoutMs,
        ["useSnmp"] = _settings.UseSnmp,
        ["snmpCommunity"] = _settings.SnmpCommunity,
        ["snmpPort"] = _settings.SnmpPort,
    };

    private Task SaveSettingsAsync(JsonElement payload)
    {
        _settings.NasIp = GetString(payload, "nasIp");
        _settings.ShareName = GetString(payload, "shareName");
        _settings.WebPort = GetInt(payload, "webPort", _settings.WebPort);
        _settings.AuthMode = GetString(payload, "authMode") ?? "current";
        _settings.Username = GetString(payload, "username");
        _settings.Password = GetString(payload, "password");
        _settings.SaveCredentials = GetBoolean(payload, "saveCredentials");
        _settings.AutoScanOnStart = GetBoolean(payload, "autoScanOnStart");
        _settings.ProbeTimeoutMs = GetInt(payload, "probeTimeoutMs", _settings.ProbeTimeoutMs);
        _settings.UseSnmp = GetBoolean(payload, "useSnmp");
        _settings.SnmpCommunity = GetString(payload, "snmpCommunity") ?? "public";
        _settings.SnmpPort = GetInt(payload, "snmpPort", _settings.SnmpPort);
        _settings.Save();
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------- push

    private void OnSpeedUpdated(IReadOnlyList<SpeedSample> samples)
    {
        if (samples.Count == 0) return;

        var targetId = _speedAdapterId ?? _adapter?.Id;
        SpeedSample? target = samples.FirstOrDefault(s => s.AdapterId == targetId);
        target ??= samples.FirstOrDefault(s => s.AdapterName.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
                 ?? samples.FirstOrDefault(s => s.AdapterName.Contains("イーサーネット"))
                 ?? samples[0];

        var adapters = new JsonArray();
        foreach (var s in samples)
        {
            adapters.Add(new JsonObject { ["id"] = s.AdapterId, ["name"] = s.AdapterName });
        }

        PushEvent("speed", new JsonObject
        {
            ["rx"] = target.RxBytesPerSecond,
            ["tx"] = target.TxBytesPerSecond,
            ["id"] = target.AdapterId,
            ["name"] = target.AdapterName,
            ["adapters"] = adapters,
        });
    }

    private void PushEvent(string name, JsonNode data)
    {
        try
        {
            Push?.Invoke(name, data);
        }
        catch (Exception ex)
        {
            AppLog.Warn("UI への push に失敗しました。", ex);
        }
    }

    // -------------------------------------------------------------- 補助

    private static string? GetString(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;

    private static int GetInt(JsonElement payload, string name, int fallback)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : fallback;

    public void Dispose()
    {
        _scanCts?.Cancel();
        _hotspotTimer.Dispose();
        _speed.Dispose();
        _relay.Dispose();
        _proxy.Dispose();
    }
}
