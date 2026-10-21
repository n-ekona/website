using System.Text;
using NasWatcher.Core;
using NasWatcher.UI;

namespace NasWatcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Shift-JIS などの OEM コードページ (net コマンドの出力) を扱えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ApplicationConfiguration.Initialize();

        AppLog.Info("=== NasWatcher 起動 ===");
        AppLog.Info($"OS: {Environment.OSVersion} / .NET: {Environment.Version}");

        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("未処理の例外が発生しました。", e.Exception);
            MessageBox.Show(
                $"予期しないエラーが発生しました。\n\n{e.Exception.Message}",
                "NAS WATCH",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("監視されていないタスク例外が発生しました。", e.Exception);
            e.SetObserved();
        };

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            AppLog.Error("起動に失敗しました。", ex);
            MessageBox.Show(ex.Message, "NAS WATCH", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            AppLog.Info("=== NasWatcher 終了 ===");
        }
    }
}
