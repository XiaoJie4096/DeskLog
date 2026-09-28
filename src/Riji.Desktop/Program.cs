using System.IO;
using System.Windows;

namespace Riji.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var production = args.Contains("--production") || (!args.Contains("--data-dir") && File.Exists(Path.Combine(AppContext.BaseDirectory, "production-default")));
        var profile = production ? "Production" : "Development";
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riji", profile);
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0)
        {
            if (production || dataIndex + 1 >= args.Length) throw new ArgumentException("自定义数据目录只适用于开发运行。");
            dataDir = Path.GetFullPath(args[dataIndex + 1]);
            profile = "Test";
        }
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant())));
        var quitEventName = "Local\\Riji-Quit-" + identity;
        var recoveryMarker = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar))!, ".Riji-Recovery-" + identity + ".json");
        if (File.Exists(recoveryMarker))
        {
            System.Windows.MessageBox.Show("数据回滚尚未完成。为避免打开不完整数据，日迹暂未启动。请保留恢复标记及数据目录，完成恢复后再启动。", "日迹恢复待完成");
            return;
        }
        if (args.Contains("--quit"))
        {
            try { using var request = EventWaitHandle.OpenExisting(quitEventName); request.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        using var mutex = new Mutex(true, "Local\\Riji-" + identity, out var first);
        if (!first) { System.Windows.MessageBox.Show("这个数据目录的日迹已经在运行，请从托盘打开。", "日迹"); return; }
        try
        {
            try { _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                var result = System.Windows.MessageBox.Show("日迹需要 Microsoft Edge WebView2 Runtime 才能显示界面。\n可运行包内 install-webview2.ps1 安装，或打开微软官网下载。\n\n现在打开微软下载页？", "缺少 WebView2", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
                return;
            }
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            if (args.Any(arg => arg is "--system-test" or "--offline-review-test") && (production || dataIndex < 0))
                throw new ArgumentException("自动验证必须指定独立测试数据目录。");
            var window = new MainWindow(dataDir, profile, args.Contains("--system-test"), args.Contains("--offline-review-test"));
            using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, quitEventName);
            var quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => app.Dispatcher.BeginInvoke(window.RequestExit), null, Timeout.Infinite, false);
            try { app.Run(window); }
            finally { quitRegistration.Unregister(null); }
        }
        catch (Exception ex)
        {
            new Riji.Infrastructure.DiagnosticLog(dataDir).Failure(Riji.Infrastructure.DiagnosticEvent.Startup, ex);
            System.Windows.MessageBox.Show("日迹未能启动：" + ex.Message, "日迹");
        }
    }
}
