using System.Runtime.InteropServices;
using System.Text;

namespace NasWatcher.Core;

/// <summary>SMB 関連の Win32 エラーコード。</summary>
public static class SmbErrors
{
    public const uint NoError = 0;
    public const uint AccessDenied = 5;
    public const uint InvalidPassword = 86;
    public const uint BadNetPath = 53;
    public const uint BadNetName = 67;
    public const uint DeviceNotConnected = 1167;
    public const uint AlreadyExists = 85;
    public const uint LogonFailure = 1326;
    public const uint AccountRestriction = 1327;
    public const uint InvalidParameter = 87;
    public const uint SessionCredentialConflict = 1219;

    /// <summary>Win32 のエラーメッセージを取得します。</summary>
    public static string Describe(uint code)
    {
        if (code == 0) return "正常";

        var message = new StringBuilder(256);
        var ok = FormatMessage(
            0x00001000 | 0x00000200, // FROM_SYSTEM | IGNORE_INSERTS
            IntPtr.Zero, code, 0, message, message.Capacity, out _);

        if (ok != 0) return message.ToString().Trim();
        return $"Win32 エラー {code}";
    }

    /// <summary>利用者向けに、原因と対処を添えた説明を作ります。</summary>
    public static string DescribeForUser(uint code) => code switch
    {
        NoError => "正常",
        BadNetName => "共有にアクセスできません。NAS のユーザー名とパスワードが必要です (設定タブで指定してください)。",
        BadNetPath => "共有が見つかりません。共有名を確認してください。",
        AccessDenied => "アクセスが拒否されました。共有のアクセス権と認証設定を確認してください。",
        LogonFailure => "ユーザー名またはパスワードが違います。設定タブの認証方式を確認してください。",
        InvalidPassword => "パスワードが違います。",
        AccountRestriction => "このアカウントでは接続できません (Guest アクセスが無効など)。",
        SessionCredentialConflict => "別の資格情報で既に接続されています。",
        AlreadyExists => "既に接続されています。",
        DeviceNotConnected => "ネットワーク切断中です。",
        InvalidParameter => "接続先が不正です。",
        _ => Describe(code),
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FormatMessage(
        uint flags, IntPtr source, uint messageId, uint languageId,
        [Out] StringBuilder buffer, int size, out uint args);
}

/// <summary>NetServerGetInfo のサーバー情報。</summary>
public sealed record ServerInfo(string Name, string Comment, int Type, int PlatformId)
{
    /// <summary>Samba / NAS 系サーバーは Linux や Samba というコメントを返すことが多い。</summary>
    public bool LooksLikeNas => Comment.Contains("Samba", StringComparison.OrdinalIgnoreCase)
                                || Comment.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                                || Comment.Contains("Synology", StringComparison.OrdinalIgnoreCase)
                                || Comment.Contains("QNAP", StringComparison.OrdinalIgnoreCase)
                                || Comment.Contains("TrueNAS", StringComparison.OrdinalIgnoreCase)
                                || Comment.Contains("ReadyNAS", StringComparison.OrdinalIgnoreCase)
                                || Comment.Contains("FreeNAS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>NAS サーバー情報の取得。</summary>
public static class SmbServerInfo
{
    private const int Level101 = 101;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVER_INFO_101
    {
        public int sv101_platform_id;
        [MarshalAs(UnmanagedType.LPWStr)] public string sv101_name;
        public int sv101_version_major;
        public int sv101_version_minor;
        public int sv101_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string sv101_comment;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetServerGetInfo(string servername, int level, out SERVER_INFO_101 buf);

    /// <summary>NAS のサーバー情報を取得します。取得できない場合は null。</summary>
    public static ServerInfo? TryGet(string ip)
    {
        try
        {
            var result = NetServerGetInfo($"\\\\{ip}", Level101, out var info);
            if (result != 0) return null;
            return new ServerInfo(
                info.sv101_name ?? string.Empty,
                info.sv101_comment ?? string.Empty,
                info.sv101_type,
                info.sv101_platform_id);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"NetServerGetInfo({ip}) に失敗しました。", ex);
            return null;
        }
    }
}
