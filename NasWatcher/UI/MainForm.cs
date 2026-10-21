using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NasWatcher.Core;

namespace NasWatcher.UI;

/// <summary>WebView2 で HTML による UI を表示するメインウィンドウ。</summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _webView = new();
    private AppBridge? _bridge;
    private CoreWebView2? _core;
    private bool _ready;

    public MainForm()
    {
        Text = "NAS WATCH";
        MinimumSize = new Size(1080, 700);
        Size = new Size(1400, 880);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(11, 15, 20);
        MinimumSize = new Size(1080, 700);
        Font = new Font("Yu Gothic UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _webView.Dock = DockStyle.Fill;
        _webView.BackColor = Color.FromArgb(11, 15, 20);
        _webView.DefaultBackgroundColor = Color.FromArgb(11, 15, 20);
        Controls.Add(_webView);

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) => Cleanup();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NasWatcher",
                "webview");
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions();
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            var assetFolder = ExtractAssets();

            _core = _webView.CoreWebView2;
            _core.SetVirtualHostNameToFolderMapping(
                "nas.local",
                assetFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _core.Settings.AreDefaultContextMenusEnabled = true;
            _core.Settings.AreDevToolsEnabled = true;
            _core.Settings.IsStatusBarEnabled = false;
            _core.Settings.IsZoomControlEnabled = false;
            _core.Settings.IsWebMessageEnabled = true;

            _bridge = new AppBridge();
            _bridge.Push += OnBridgePush;

            _core.WebMessageReceived += OnWebMessageReceived;
            _core.NavigationCompleted += (_, _) => _ready = true;

            _core.Navigate("https://nas.local/index.html");
        }
        catch (Exception ex)
        {
            AppLog.Error("Web UI の初期化に失敗しました。", ex);
            ShowFallback(ex);
        }
    }

    private void ShowFallback(Exception ex)
    {
        Controls.Clear();
        var message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(230, 237, 245),
            Font = new Font("Yu Gothic UI", 10F),
            Text = "画面を表示できませんでした。\r\n\r\n"
                 + "Microsoft Edge WebView2 Runtime が必要です。\r\n"
                 + $"https://developer.microsoft.com/microsoft-edge/webview2/\r\n\r\n{ex.Message}",
        };
        Controls.Add(message);
    }

    // -------------------------------------------------------------- ブリッジ

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            payload = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Web メッセージの解析に失敗しました。", ex);
            return;
        }

        var id = payload.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
        var action = payload.TryGetProperty("action", out var actionElement) ? actionElement.GetString() ?? "" : "";
        var body = payload.TryGetProperty("payload", out var bodyElement) ? bodyElement : default;

        JsonNode? result = null;
        string? error = null;

        try
        {
            result = _bridge is null ? null : await _bridge.HandleAsync(action, body).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"コマンド '{action}' の実行に失敗しました。", ex);
            error = ex.Message;
        }

        try
        {
            var response = new JsonObject
            {
                ["id"] = id,
                ["ok"] = error is null,
                ["data"] = result,
            };

            if (error is not null) response["error"] = error;

            var json = response.ToJsonString();
            _core?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Web UI への応答に失敗しました。", ex);
        }
    }

    private void OnBridgePush(string name, JsonNode data)
    {
        if (!_ready || _core is null) return;

        void Post()
        {
            try
            {
                var message = new JsonObject
                {
                    ["event"] = name,
                    ["data"] = data.DeepClone(),
                };
                _core?.PostWebMessageAsJson(message.ToJsonString());
            }
            catch (Exception ex)
            {
                AppLog.Warn("UI への push に失敗しました。", ex);
            }
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(Post));
            }
            else
            {
                Post();
            }
        }
        catch (InvalidOperationException)
        {
            // ウィンドウが破棄された直後
        }
    }

    // -------------------------------------------------------------- アセット

    /// <summary>埋め込みリソース (HTML/CSS/JS) をローカルフォルダーに展開します。</summary>
    private static string ExtractAssets()
    {
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NasWatcher",
            "ui");

        Directory.CreateDirectory(target);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "ui.index.html", "ui.styles.css", "ui.app.js" })
        {
            var fileName = name["ui.".Length..];
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            var path = Path.Combine(target, fileName);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) continue;

            File.WriteAllBytes(path, bytes);
        }

        return target;
    }

    private void Cleanup()
    {
        if (_bridge is not null)
        {
            _bridge.Push -= OnBridgePush;
            _bridge.Dispose();
            _bridge = null;
        }
    }
}
