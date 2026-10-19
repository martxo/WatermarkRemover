using System.Windows;
using System.Windows.Threading;
using Velopack;
using WatermarkRemover.App.Logging;
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

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static string GetVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>UI thread crash: log, tell the user, and keep the window alive.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("界面线程未处理异常", e.Exception);
        ShowUnexpectedErrorDialog(e.Exception);
        e.Handled = true;
    }

    /// <summary>Fatal non-UI failure: nothing to save, but the log must survive the crash.</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("后台线程致命异常", e.ExceptionObject as Exception);
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
