using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;

    /// <summary>Shared startup stopwatch: every "距启动 X ms" log line reads against this, so
    /// the startup profile in the log is one comparable column across versions.</summary>
    public static readonly Stopwatch BootClock = Stopwatch.StartNew();

    /// <summary>Why this process is ending; the OnExit line names it so a log never just says
    /// "应用退出" after a crash or an updater hand-off.</summary>
    private static string _exitReason = "用户关闭窗口";

    /// <summary>
    /// Records the reason of an imminent exit and flushes it immediately: updaters terminate via
    /// Environment.Exit (no OnExit), and a fatal crash may not reach the exit handler either.
    /// </summary>
    public static void NoteExitReason(string reason)
    {
        _exitReason = reason;
        AppLog.Info($"退出原因：{reason}");
        AppLog.Flush();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Must run before anything else: Velopack's install/update hooks intercept --veloapp-*
        // arguments and fast-exit the process, which only works before any file IO or window is
        // created. Harmless no-op for green-directory (non-installed) runs.
        VelopackApp.Build().Run();

        // 「疑难重启」拉起的新实例：旧实例还在关闭（停后台服务、释放单实例互斥体），先等它
        // 真正退出，否则新实例会把自己当成第二次启动而直接退出。
        var restartedFrom = TroubleshootRestartService.WaitForPreviousInstance(e.Args);

        NetdiskAdminAccess.Initialize(e.Args);
        if (NetdiskAdminAccess.IsRequested && !NetdiskAdminAccess.IsEnabled)
        {
            MessageBox.Show(
                "网盘管理入口需要同时使用特殊启动参数和“以管理员身份运行”。",
                "办公助理", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
        }

        // A second double-click must not open another copy. An administrator launch never sends
        // its privileged argument to an existing ordinary process, nor starts a second backup.
        // The exit-request callback is the hotkey hand-off: the elevated copy asks the running
        // ordinary copy to quit so the user never has to find the tray menu themselves.
        _instanceGuard = new SingleInstanceGuard(
            signalExistingInstance: !NetdiskAdminAccess.IsRequested,
            onExitRequested: () => (MainWindow as MainWindow)?.ExitForAdminSwitch());
        if (!_instanceGuard.IsFirst)
        {
            if (NetdiskAdminAccess.IsRequested &&
                TryTakeOverAfterExistingInstanceExit())
            {
                AppLog.Info("管理员入口已接替正在运行的普通实例");
            }
            else
            {
                if (NetdiskAdminAccess.IsRequested)
                {
                    MessageBox.Show(
                        "请先从托盘退出当前办公助理，再以管理员方式启动网盘管理入口。",
                        "办公助理", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _instanceGuard.Dispose();
                Environment.Exit(0);
            }
        }

        base.OnStartup(e);

        AppFolders.EnsureRoot();
        AppLog.Initialize();
        BootClock.Restart();
        AppLog.Info($"应用启动 v{GetVersion()}，数据目录：{AppFolders.Root}");
        if (restartedFrom is { } previous)
        {
            AppLog.Info(previous switch
            {
                { Exited: true, Forced: true } =>
                    $"疑难重启：旧进程 {previous.ProcessId} 未按时退出，已强制结束，新实例已启动",
                { Exited: true } =>
                    $"疑难重启：旧进程 {previous.ProcessId} 已退出，新实例已启动",
                _ => $"疑难重启：旧进程 {previous.ProcessId} 无法结束，仍继续启动",
            });
        }
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
        // A stable %APPDATA% CLI config survives every update. On a fresh machine, a dropped-in
        // login-pack.json is imported here, so the cloud channel works without another scan.
        AliyunpanCliRuntime.EnsureStableConfig();
        CloudUpdateService.TryAutoImportLoginPack();

        // The detached 云更新 script writes its verdict after this process exited; fold it into
        // the install history on the first start of the new (or restored) version.
        CloudUpdateService.ReconcileLastApply();

        // Velopack applies updates with Environment.Exit, which bypasses OnExit/AppLog.Shutdown —
        // these startup facts must already be on disk before any updater can replace this process.
        AppLog.Flush();

        // Environment baseline: without these three facts a support log cannot distinguish a
        // code defect from a machine-specific component/OS problem.
        AppLog.Info($"运行环境：{RuntimeInformation.OSDescription}；" +
                    $"{RuntimeInformation.ProcessArchitecture}；" +
                    $"{Environment.ProcessorCount} 逻辑核；" +
                    $"用户 {Environment.UserName}@{Environment.MachineName}；" +
                    $"区域 {CultureInfo.CurrentCulture.Name}；.NET {Environment.Version}");
        AppLog.Debug($"日志级别：{AppLog.MinimumLevel}；内存[启动]：{AppLog.MemorySnapshot()}");

        // 每台装了本软件的电脑每 10 分钟上报桌面备份概况（备份与征信页）。放在进程里而不是
        // 每台机器手工装计划任务：随软件更新自动覆盖所有电脑，也不闪控制台窗口。
        BackupStatsReporter.Shared.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Closing to tray must never swallow a Windows shutdown/logoff.
        SessionEnding += (_, _) =>
        {
            if (MainWindow is MainWindow window) window.AllowExitOnSystemShutdown();
        };

        // The window is created here rather than through StartupUri: a logon background start
        // must obtain a window handle (tray icon, hotkeys, shelf) WITHOUT presenting a first
        // frame. See MainWindow.BeginBackgroundHost for why that distinction matters.
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (NetdiskAdminAccess.IsBackground)
        {
            mainWindow.BeginBackgroundHost();
        }
        else
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BackupStatsReporter.Shared.Dispose();
        AppLog.Info($"应用退出（{_exitReason}）");
        AppLog.Shutdown();
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Admin-launch hand-off: the running ordinary copy was asked to exit; poll for the
    /// single-instance mutex it releases on shutdown (the graceful exit stops the backup
    /// service first, so this normally completes within a second or two).
    /// </summary>
    private bool TryTakeOverAfterExistingInstanceExit()
    {
        _instanceGuard?.Dispose();
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Thread.Sleep(300);
            var guard = new SingleInstanceGuard(signalExistingInstance: false);
            if (guard.IsFirst)
            {
                _instanceGuard = guard;
                return true;
            }

            guard.Dispose();
        }

        return false;
    }

    private static string GetVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>UI thread crash: log, flush, tell the user, and keep the window alive.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("界面线程未处理异常", e.Exception);
        AppLog.Flush(); // the crash may be followed by a kill: get the stack on disk first
        CrashReporter.Capture(e.Exception, "界面线程未处理异常");
        ShowUnexpectedErrorDialog(e.Exception);
        e.Handled = true;
    }

    /// <summary>Fatal non-UI failure: nothing to save, but the log must survive the crash.</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("后台线程致命异常", e.ExceptionObject as Exception);
        CrashReporter.Capture(e.ExceptionObject as Exception, "后台线程致命异常");
        NoteExitReason($"后台线程致命异常（{e.ExceptionObject?.GetType().Name}）");
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
