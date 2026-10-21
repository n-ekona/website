using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasWatcher.Core;

/// <summary>アプリケーションの設定 (JSON で永続化)。</summary>
public sealed class AppSettings
{
    /// <summary>認証方式: current=現在ユーザー / guest=ゲスト(匿名) / explicit=ユーザー名とパスワード。</summary>
    public string AuthMode { get; set; } = "current";

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Windows の資格情報マネージャーに保存する。</summary>
    public bool SaveCredentials { get; set; } = true;

    /// <summary>NAS の IP アドレス。</summary>
    public string? NasIp { get; set; }

    /// <summary>既定で選択する SMB 共有名。</summary>
    public string? ShareName { get; set; }

    /// <summary>NAS の Web 管理画面ポート。</summary>
    public int WebPort { get; set; } = 5000;

    /// <summary>ポート探索のタイムアウト(ミリ秒)。</summary>
    public int ProbeTimeoutMs { get; set; } = 400;

    /// <summary>SMB で取得できない場合に SNMP で容量を取得するか。</summary>
    public bool UseSnmp { get; set; } = true;

    /// <summary>SNMP のコミュニティ文字列。</summary>
    public string SnmpCommunity { get; set; } = "public";

    /// <summary>SNMP のポート番号。</summary>
    public int SnmpPort { get; set; } = 161;

    /// <summary>起動時に自動スキャンするか。</summary>
    public bool AutoScanOnStart { get; set; } = true;

    /// <summary>最後にコピーした SMB パス。</summary>
    public string? LastSmbPath { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NasWatcher",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("設定の読み込みに失敗しました。既定値を使用します。", ex);
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("設定の保存に失敗しました。", ex);
        }
    }
}
