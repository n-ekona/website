using System.Collections.Concurrent;
using System.Text;

namespace NasWatcher.Core;

/// <summary>ファイルと画面の両方に出す簡易ログ。</summary>
public static class AppLog
{
    private static readonly ConcurrentQueue<string> Recent = new();
    private static readonly object FileLock = new();
    private const int MaxRecent = 500;

    /// <summary>新しいログ行が発生したときに発火します (UI スレッド.Invoke が必要)。</summary>
    public static event Action<string>? Logged;

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NasWatcher",
        "naswatcher.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + ex;
        }

        Recent.Enqueue(line);
        while (Recent.Count > MaxRecent && Recent.TryDequeue(out _)) { }

        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ログ出力失敗で本体が落ちないようにする
            }
        }

        try
        {
            Logged?.Invoke(line);
        }
        catch
        {
            // _ui 側の例外で本体が落ちないようにする
        }
    }

    /// <summary>直近のログを新しい順に返します。</summary>
    public static IReadOnlyList<string> GetRecent(int count = 300)
        => Recent.Reverse().Take(count).ToArray();
}
