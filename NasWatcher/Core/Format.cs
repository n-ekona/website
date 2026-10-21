namespace NasWatcher.Core;

/// <summary>バイト数や速度の表示用フォーマッタ。</summary>
public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>バイト数を "1.23 GB" 形式にします。</summary>
    public static string Bytes(double bytes)
    {
        if (double.IsNaN(bytes) || double.IsInfinity(bytes)) return "-";
        var negative = bytes < 0;
        bytes = Math.Abs(bytes);

        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        var text = unit == 0 ? bytes.ToString("0") : bytes.ToString("0.##");
        return (negative ? "-" : "") + text + " " + Units[unit];
    }

    /// <summary>速度を "12.34 MB/s (103 Mbps)" 形式にします。</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond)) return "-";

        var mbps = bytesPerSecond * 8.0 / 1_000_000.0;
        var mbpsText = mbps >= 100 ? mbps.ToString("0") : mbps.ToString("0.0");
        return $"{Bytes(bytesPerSecond)}/s ({mbpsText} Mbps)";
    }

    /// <summary>ミリ秒の応答時間を表示します。</summary>
    public static string Milliseconds(double ms) => ms < 0 ? "-" : $"{ms:0} ms";
}
