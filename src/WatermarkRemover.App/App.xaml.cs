using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Must run before anything else: Velopack's install/update hooks intercept --veloapp-*
        // arguments and fast-exit the process, which only works before any file IO or window is
        // created. Harmless no-op for green-directory (non-installed) runs.
        VelopackApp.Build().Run();

        base.OnStartup(e);

        AppFolders.EnsureRoot();
        AppLog.Initialize();
        AppLog.Info($"应用启动 v{GetVersion()}，数据目录：{AppFolders.Root}");
        // Where this process runs and what Velopack thinks is installed: the two answers differ
        // whenever an old shortcut or a second copy is launched, which otherwise looks exactly
        // like "the update downloaded but the version never changed".
        AppLog.Info($"安装信息：{VelopackUpdateService.DescribeInstall()}");
        if (VelopackUpdateService.InstalledManifestVersion is { } manifest
            && manifest > UpdateService.CurrentVersion)
        {
            AppLog.Error($"安装清单为 v{manifest}，正在运行的程序却是 v{UpdateService.CurrentVersion}：" +
                         "更新只替换了一部分文件，请重新安装最新版。");
        }

        // Environment baseline: without these three facts a support log cannot distinguish a
        // code defect from a machine-specific component/OS problem.
        AppLog.Info($"运行环境：{RuntimeInformation.OSDescription}；" +
                    $"{RuntimeInformation.ProcessArchitecture}；" +
                    $"{Environment.ProcessorCount} 逻辑核；" +
                    $"用户 {Environment.UserName}@{Environment.MachineName}；" +
                    $"区域 {CultureInfo.CurrentCulture.Name}；.NET {Environment.Version}");
        AppLog.Debug($"日志级别：{AppLog.MinimumLevel}；内存[启动]：{AppLog.MemorySnapshot()}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("应用退出");
        AppLog.Shutdown();
        base.OnExit(e);
    }

    private static string GetVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>UI thread crash: log, flush, tell the user, and keep the window alive.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("界面线程未处理异常", e.Exception);
        AppLog.Flush(); // the crash may be followed by a kill: get the stack on disk first
        ShowUnexpectedErrorDialog(e.Exception);
        e.Handled = true;
    }

    /// <summary>Fatal non-UI failure: nothing to save, but the log must survive the crash.</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("后台线程致命异常", e.ExceptionObject as Exception);
        AppLog.Flush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("未观察的任务异常", e.Exception);
        e.SetObserved();
    }

    private static void ShowUnexpectedErrorDialog(Exception ex)
    {
        MessageBox.Show(
            "程序遇到意外错误，已自动记录日志，您可以继续使用。\n\n" +
            $"{ex.Message}\n\n" +
            $"日志位置：{AppFolders.LogDirectory}",
            "办公助理",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
