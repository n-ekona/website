using System.Runtime.InteropServices;
using System.Text;

namespace NasWatcher.Core;

/// <summary>SMB 共有の列挙・接続・空き容量取得。</summary>
public sealed class SmbClient
{
    private const uint RESOURCE_CONNECTED = 0x00000001;
    private const uint RESOURCE_GLOBALNET = 0x00000002;
    private const uint RESOURCE_REMEMBERED = 0x00000010;
    private const uint RESOURCETYPE_DISK = 0x00000001;
    private const uint CONNECT_UPDATE_PROFILE = 0x00000001;
    private const uint CONNECT_TEMPORARY = 0x00000004;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public uint dwScope;
        public uint dwType;
        public uint dwDisplayType;
        public uint dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WNetCancelConnection2(string? name, uint flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WNetOpenEnum(uint scope, uint type, uint usage, ref NETRESOURCE netResource, out IntPtr enumHandle);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WNetEnumResource(IntPtr enumHandle, out uint count, [Out] NETRESOURCE[]? resources, ref uint bufferSize);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WNetCloseEnum(IntPtr enumHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    /// <summary>NAS の空き容量情報。</summary>
    public sealed record Capacity(ulong FreeBytes, ulong TotalBytes, ulong TotalFreeBytes)
    {
        public ulong UsedBytes => TotalBytes > TotalFreeBytes ? TotalBytes - TotalFreeBytes : 0;

        public double UsedRatio => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
    }

    /// <summary>実際に試す 1 つの認証。</summary>
    public sealed record AuthAttempt(string Label, string? User, string? Pass);

    /// <summary>認証情報。</summary>
    public sealed record Credentials(string Mode, string? Username, string? Password, bool Persist)
    {
        public static Credentials FromSettings(AppSettings settings) => new(
            settings.AuthMode,
            settings.Username,
            settings.Password,
            settings.SaveCredentials);

        /// <summary>接続を試す認証の候補 (設定された方式を最初に、必要なら代替も試す)。</summary>
        public IReadOnlyList<AuthAttempt> Attempts()
        {
            var list = new List<AuthAttempt>();

            switch (Mode)
            {
                case "guest":
                    list.Add(new AuthAttempt("ゲスト (匿名)", string.Empty, string.Empty));
                    break;
                case "explicit":
                    if (!string.IsNullOrWhiteSpace(Username))
                    {
                        list.Add(new AuthAttempt("指定のユーザー", Username, Password ?? string.Empty));
                    }
                    break;
                default:
                    list.Add(new AuthAttempt("現在ログイン中のユーザー", null, null));
                    break;
            }

            // 代替の認証も試す (NAS によって必要になる場合があるため)
            if (!list.Any(a => a.User is null or "" && a.Label.StartsWith("ゲスト", StringComparison.Ordinal)))
            {
                list.Add(new AuthAttempt("ゲスト (匿名)", string.Empty, string.Empty));
            }

            if (!list.Any(a => a.User is null && a.Label.StartsWith("現在", StringComparison.Ordinal)))
            {
                list.Add(new AuthAttempt("現在ログイン中のユーザー", null, null));
            }

            if (Mode == "explicit" && !string.IsNullOrWhiteSpace(Username))
            {
                list.Add(new AuthAttempt("指定のユーザー", Username, Password ?? string.Empty));
            }

            return list;
        }
    }

    /// <summary>接続結果。</summary>
    public sealed record ConnectResult(bool Success, uint Error, string? UsedLabel, string Message)
    {
        public static ConnectResult Ok(string label) => new(true, 0, label, "接続できました");
    }

    /// <summary>容量取得の結果。</summary>
    public sealed record CapacityResult(bool Success, Capacity? Capacity, string Message, string? AuthLabel);

    // ------------------------------------------------------------------ 共有列挙

    /// <summary>指定 NAS の共有名一覧を取得します。</summary>
    public List<string> ListShares(string ip, Credentials credentials)
    {
        var shares = new List<string>();

        // IPC$ への接続を試みる (共有列挙に必要です)
        TryConnect($@"\\{ip}\IPC$", credentials);

        var resource = new NETRESOURCE
        {
            dwScope = RESOURCE_CONNECTED,
            dwType = RESOURCETYPE_DISK,
            dwUsage = RESOURCETYPE_DISK,
            lpRemoteName = $@"\\{ip}",
        };

        IntPtr handle = IntPtr.Zero;
        try
        {
            var result = WNetOpenEnum(RESOURCE_CONNECTED, RESOURCETYPE_DISK, 0, ref resource, out handle);
            if (result != 0)
            {
                AppLog.Warn($"共有列挙 (WNetOpenEnum) に失敗しました: {SmbErrors.Describe(result)}");
            }
            else
            {
                uint bufferSize = 16384;
                var buffer = new NETRESOURCE[64];
                while (true)
                {
                    uint count;
                    var enumResult = WNetEnumResource(handle, out count, buffer, ref bufferSize);
                    if (enumResult != 0 || count == 0) break;

                    for (var i = 0; i < count; i++)
                    {
                        var name = buffer[i].lpRemoteName;
                        if (string.IsNullOrEmpty(name)) continue;

                        var trimmed = name.TrimEnd('\\');
                        var slash = trimmed.LastIndexOf('\\');
                        var share = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
                        if (string.IsNullOrWhiteSpace(share)) continue;
                        if (share.Equals("IPC$", StringComparison.OrdinalIgnoreCase)) continue;

                        // サーバー名そのもの (\\192.168.1.10) は共有ではない
                        if (share.Contains('\\') || share.Contains('/') || share.Contains(':')) continue;
                        if (share.Equals(trimmed, StringComparison.OrdinalIgnoreCase) && trimmed.StartsWith("\\\\", StringComparison.Ordinal)) continue;

                        if (!shares.Contains(share, StringComparer.OrdinalIgnoreCase))
                        {
                            shares.Add(share);
                        }
                    }
                }
            }
        }
        finally
        {
            if (handle != IntPtr.Zero) WNetCloseEnum(handle);
        }

        if (shares.Count == 0)
        {
            // P/Invoke で取れなかった場合は net view の出力を解析する
            foreach (var share in ListSharesViaNetView(ip))
            {
                if (!shares.Contains(share, StringComparer.OrdinalIgnoreCase)) shares.Add(share);
            }
        }

        shares.Sort(StringComparer.OrdinalIgnoreCase);
        return shares;
    }

    /// <summary>"net view" コマンドの出力から共有名を読み取ります (フォールバック用)。</summary>
    private static IEnumerable<string> ListSharesViaNetView(string ip)
    {
        var result = new List<string>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("net", $"view \\\\{ip}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleEncoding(),
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return result;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            foreach (var raw in output.Split('\r', '\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("---") || line.StartsWith("===")) continue;

                // 共有の行は「共有名 種類 コメント」のように 2 列以上ある。
                // 1 列だけの行 (サーバー名やエラーメッセージ) は共有ではない。
                var columns = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2) continue;

                var name = columns[0].Trim();
                if (name.Length == 0) continue;

                // ヘッダーやメッセージ (共有名ではありません) を除外する
                // ※ 共有名そのものが "share" のような場合があるため、完全一致での判定にする
                if (name is "共有名" or "Share Name" or "ShareName" or "コマンド" or "The" or "The command"
                    or "Error" or "System error" or "システムエラー")
                {
                    continue;
                }

                if (name.Contains("存在", StringComparison.Ordinal)
                    || name.Contains("見つかり", StringComparison.Ordinal)
                    || name.Contains("ません", StringComparison.Ordinal)
                    || name.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 共有名として使える文字だけ (英数字・記号・日本語) に絞る
                if (name.Any(c => char.IsControl(c))) continue;

                // サーバー名やポート付き表記 (\\192.168.1.10:6000) は共有ではない
                if (name.Contains('\\') || name.Contains('/') || name.Contains(':')) continue;

                if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("net view の実行に失敗しました。", ex);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>コンソール (net コマンド) の出力に使う OEM 文字コード。</summary>
    private static Encoding ConsoleEncoding()
    {
        try
        {
            var codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (codePage > 0) return Encoding.GetEncoding(codePage);
        }
        catch
        {
            // 対応するコードページが無い場合は既定値を使う
        }

        return Encoding.Default;
    }

    // ------------------------------------------------------------------ 接続

    /// <summary>認証の候補を順に試し、接続に成功した結果を返します。</summary>
    public ConnectResult Connect(string remotePath, Credentials credentials)
    {
        uint lastError = SmbErrors.InvalidParameter;

        foreach (var attempt in credentials.Attempts())
        {
            var error = TryConnect(remotePath, attempt);
            if (error == 0)
            {
                return ConnectResult.Ok(attempt.Label);
            }

            if (error == ERROR_ALREADY_ASSIGNED || error == ERROR_SESSION_CREDENTIAL_CONFLICT)
            {
                // 既に接続済みのため成功扱い
                return ConnectResult.Ok(attempt.Label);
            }

            lastError = error;
            AppLog.Info($"接続失敗 ({remotePath} / {attempt.Label}): {SmbErrors.Describe(error)}");
        }

        return new ConnectResult(false, lastError, null, SmbErrors.DescribeForUser(lastError));
    }

    /// <summary>指定の認証で 1 回だけ接続を試みます。0 = 成功。</summary>
    public uint TryConnect(string remotePath, AuthAttempt attempt, bool persist = false)
    {
        var resource = new NETRESOURCE
        {
            dwType = RESOURCETYPE_DISK,
            lpRemoteName = remotePath,
        };

        var flags = persist ? CONNECT_UPDATE_PROFILE : CONNECT_TEMPORARY;

        try
        {
            return WNetAddConnection2(ref resource, attempt.Pass, attempt.User, flags);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"接続 ({remotePath}) に例外が発生しました。", ex);
            return SmbErrors.InvalidParameter;
        }
    }

    private uint TryConnect(string remotePath, Credentials credentials)
    {
        foreach (var attempt in credentials.Attempts())
        {
            var error = TryConnect(remotePath, attempt, credentials.Persist);
            if (error == 0 || error == ERROR_ALREADY_ASSIGNED || error == ERROR_SESSION_CREDENTIAL_CONFLICT)
            {
                return error;
            }
        }

        return SmbErrors.LogonFailure;
    }

    /// <summary>接続を解除します。</summary>
    public void Disconnect(string remotePath)
    {
        try
        {
            WNetCancelConnection2(remotePath, CONNECT_UPDATE_PROFILE, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"接続解除 ({remotePath}) に失敗しました。", ex);
        }
    }

    // ------------------------------------------------------------------ 容量

    /// <summary>共有の空き容量を取得します。</summary>
    public CapacityResult GetCapacity(string ip, string share, Credentials credentials)
    {
        var remotePath = $@"\\{ip}\{share}";

        var connect = Connect(remotePath, credentials);
        if (!connect.Success)
        {
            return new CapacityResult(false, null, connect.Message, null);
        }

        // UNC パスで空き容量を取得する (共有のボリューム全体の情報)
        if (GetDiskFreeSpaceExW(remotePath, out var freeToCaller, out var total, out var totalFree))
        {
            return new CapacityResult(true, new Capacity(freeToCaller, total, totalFree), "取得しました", connect.UsedLabel);
        }

        var error = (uint)Marshal.GetLastWin32Error();
        AppLog.Warn($"GetDiskFreeSpaceEx({remotePath}) に失敗しました: {SmbErrors.Describe(error)}");

        // 共有直下で再試行する
        if (GetDiskFreeSpaceExW(remotePath + "\\", out freeToCaller, out total, out totalFree))
        {
            return new CapacityResult(true, new Capacity(freeToCaller, total, totalFree), "取得しました", connect.UsedLabel);
        }

        return new CapacityResult(false, null,
            $"容量を取得できませんでした (Win32 {error})。{SmbErrors.DescribeForUser(error)}",
            connect.UsedLabel);
    }
}
