using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Velopack;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// End-to-end update flow shared by the startup check and the manual "check for updates"
/// button: query the channel, prompt, download with visible progress, verify, swap and restart.
/// Every phase the user waits through is reflected in the progress window — the only silent step
/// is the startup check itself.
/// </summary>
public static class UpdateCoordinator
{
    private static readonly UpdateService Service = new();

    /// <summary>Velopack channel (增量推送); only used when the app is Velopack-installed.</summary>
    private static readonly VelopackUpdateService VelopackService = new();

    /// <summary>1 when a check/update is already running; guards re-entrant triggers.</summary>
    private static int _running;

    /// <summary>
    /// Startup path for an update that was downloaded but never applied — the user closed the app
    /// instead of clicking the restart icon, or Update.exe failed to swap the files. Without this
    /// the staged package sat in the cache forever while every restart kept the old version, which
    /// is exactly the "installed and restarted, still 1.16.24" report. Returns true when startup
    /// should stop here: the app is either restarting into the update or a warning was shown.
    /// </summary>
    private static bool TryApplyPendingUpdate(IDialogService dialogs)
    {
        VelopackAsset? pending;
        try
        {
            pending = VelopackUpdateService.PendingRestart;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取待应用更新失败：{ex.Message}");
            return false;
        }
        if (pending is null) return false;

        string target = pending.Version.ToString();
        string running = UpdateService.CurrentVersion.ToString();
        AppLog.Info($"发现已下载待应用的更新 v{target}（正在运行 v{running}）；" +
                    VelopackUpdateService.DescribeInstall());

        string guardDir = Path.Combine(AppFolders.Root, "updates");
        if (!UpdateApplyGuard.ShouldAttempt(
                guardDir, target, running, Environment.ProcessPath ?? string.Empty, DateTime.UtcNow))
        {
            string root = VelopackUpdateService.InstallRoot ?? "未识别";
            AppLog.Error($"更新 v{target} 已下载但重启后仍未应用，停止自动重试。" +
                         $"运行位置 {Environment.ProcessPath}，安装根目录 {root}");
            dialogs.Warn(
                $"新版本 v{target} 已下载，但重启后仍在运行 v{running}。\n\n" +
                "可能的原因：\n" +
                $"• 当前启动的不是安装版程序（运行位置：{Environment.ProcessPath}）；\n" +
                "• 安装目录无法写入，更新程序没能替换文件。\n\n" +
                $"安装位置：{root}\n\n" +
                "请从桌面或开始菜单的「办公助理」重新打开；仍不行请到发布页下载最新安装包重新安装。",
                "软件更新");
            return true;
        }

        try
        {
            AppLog.Flush(); // Update.exe exits this process without OnExit — get the facts on disk
            VelopackService.ApplyPendingAndRestart(pending);
            return true; // the process exits inside the call
        }
        catch (Exception ex)
        {
            AppLog.Error($"启动时应用更新 v{target} 失败", ex);
            dialogs.Warn(
                $"更新 v{target} 已下载，但启动时应用失败。\n\n原因：{ex.Message}\n\n" +
                $"运行位置：{Environment.ProcessPath}\n" +
                $"安装位置：{VelopackUpdateService.InstallRoot ?? "未识别"}\n\n" +
                "可到设置中重新检查更新，或到发布页下载最新安装包。",
                "软件更新");
            return true;
        }
    }

    /// <summary>
    /// End-to-end update flow shared by the startup check and the manual "check for updates"
    /// button: query the channel, prompt, download with visible progress, verify, swap and restart.
    /// Every phase the user waits through is reflected in the progress window — the only silent step
    /// is the startup check itself.
    /// </summary>
    public static async Task RunCheckAsync(Window? owner, AppSettings settings, bool silent)
    {
        var dialogs = new DialogService(owner);

        // A package downloaded by an earlier session is applied before anything else: this is
        // what makes a plain close-and-reopen ("安装后重启") actually update. Startup checks being
        // disabled or the network being down must not leave a verified package unapplied.
        if (silent && TryApplyPendingUpdate(dialogs)) return;

        if (!UpdateConfig.IsConfigured)
        {
            AppLog.Info("更新源未配置，跳过检查更新");
            if (!silent)
                dialogs.Info("更新源尚未配置（GitHub 仓库地址待填入）。", "软件更新");
            return;
        }

        if (!settings.CheckUpdatesOnStartup && silent) return;

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            AppLog.Info("更新检查已在进行中，忽略重复触发");
            // A background or manual download may still be running after its window was closed;
            // silence here read as a dead button on the machine that reported it.
            if (!silent)
                dialogs.Info("更新检查或下载正在进行，请稍候。\n完成后标题栏的更新图标会给出提示。", "软件更新");
            return;
        }

        try
        {
            await RunCheckCoreAsync(owner, dialogs, settings, silent).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task RunCheckCoreAsync(
        Window? owner, IDialogService dialogs, AppSettings settings, bool silent)
    {
        // A downloaded update is already waiting: a manual click means "restart into it", and a
        // background pass has nothing left to do (never download the same package twice).
        if (UpdateReadyNotifier.Current.HasUpdate)
        {
            if (!silent)
            {
                ApplyReady(dialogs);
            }
            else
            {
                AppLog.Info($"v{UpdateReadyNotifier.Current.Version} 已下载待重启，跳过启动检查");
            }
            return;
        }

        // 阿里云盘 is the primary channel: file-level chunks, version records and rollback. It
        // only declines (false) when no account is configured or the cloud repository is
        // unreachable, so GitHub/Velopack stay as the fallback.
        if (await CloudUpdateCoordinator.TryRunAsync(owner, dialogs, settings, silent).ConfigureAwait(true))
        {
            return;
        }

        // Installed (Setup.exe) users ride the Velopack incremental channel; green-directory
        // users keep the legacy full-zip flow below. Both share the same UI scaffolding.
        if (VelopackUpdateService.IsChannelUsable)
        {
            await RunVelopackCheckCoreAsync(owner, dialogs, silent).ConfigureAwait(true);
            return;
        }

        // The manual path opens the progress window up front: the channel query can take up to
        // 15 seconds and must not look like a frozen button. The startup path stays invisible.
        UpdateProgressWindow? checkingWindow = null;
        if (!silent)
        {
            checkingWindow = new UpdateProgressWindow { Owner = owner };
            checkingWindow.ShowStage(UpdateStage.Checking);
            checkingWindow.Show();
        }

        UpdateCheckResult result;
        try
        {
            result = await Service.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            checkingWindow?.Close();
            LogCheckFailure(ex);
            if (!silent)
                dialogs.Warn(
                    "检查更新时出错，可能是网络不稳定。\n可稍后重试，或到发布页手动下载。\n\n" +
                    $"详情：{ex.Message}",
                    "软件更新");
            return;
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable:
                break;

            case UpdateCheckStatus.UpToDate:
                checkingWindow?.Close();
                AppLog.Info($"当前已是最新版本 v{UpdateService.CurrentVersion}");
                if (!silent)
                    dialogs.Info(
                        $"当前已是最新版本（v{UpdateService.CurrentVersion}）。\n" +
                        $"更新通道最新发布：v{result.ChannelLatestVersion}",
                        "软件更新");
                return;

            case UpdateCheckStatus.NoUpdateSource:
                checkingWindow?.Close();
                AppLog.Info($"更新源不可用：{result.Message}");
                if (!silent)
                    dialogs.Info(
                        $"暂无可用的更新来源。\n\n{result.Message}",
                        "软件更新");
                return;

            default: // CheckFailed
                checkingWindow?.Close();
                AppLog.Info($"检查更新失败：{result.Message}");
                if (!silent)
                    dialogs.Warn(
                        "暂时无法连接更新服务器，请检查网络后重试。\n\n" +
                        $"详情：{result.Message}",
                        "软件更新");
                return;
        }

        UpdateInfo info = result.Update!;
        AppLog.Info($"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}）");

        if (silent)
        {
            await DownloadSilentlyAsync(info).ConfigureAwait(true);
            return;
        }

        // The confirmation belongs to the check, not to the download: close the "checking…"
        // window so a modal dialog cannot sit on top of a live progress window.
        checkingWindow?.Close();

        string sizeText = info.SizeBytes > 0 ? $"约 {ProgressTextFormatter.FormatBytes(info.SizeBytes)}" : "未知大小";
        // A green copy cannot replace its own running exe; the Setup installs the app properly
        // (and the shortcut it creates is the copy that gets future incremental updates).
        string setupHint = info.IsSetup
            ? "\n\n当前是从解压目录直接运行，更新会把程序安装到系统默认位置并自动打开新版本；" +
              "之后请使用桌面或开始菜单的「办公助理」。"
            : string.Empty;
        bool accepted = dialogs.ConfirmYesNo(
            $"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}），是否立即下载并更新？\n\n" +
            $"更新包大小：{sizeText}\n" +
            "下载、校验均有进度显示，更新完成后程序会自动重启，无需重新安装。" + setupHint,
            "软件更新");

        if (!accepted) return;

        await DownloadAndApplyAsync(owner, dialogs, info).ConfigureAwait(true);
    }

    private static async Task DownloadAndApplyAsync(Window? owner, IDialogService dialogs, UpdateInfo info)
    {
        string version = info.Version.ToString();
        var progressWindow = new UpdateProgressWindow();
        if (owner is not null) progressWindow.Owner = owner;
        // This Show() is the whole reason a progress window exists: without it the download runs
        // invisible (regression fixed in v1.8.0).
        progressWindow.Show();
        progressWindow.Report(new UpdateProgress(UpdateStage.Downloading, 0, 0, info.SizeBytes, null, null));

        var notifier = UpdateReadyNotifier.Current;
        notifier.BeginDownloading(version);

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (progressWindow.IsLoaded) progressWindow.Report(p);
                notifier.ReportProgress(ToPercent(p));
            });
            string downloadedPath = await UpdateService
                .DownloadUpdateAsync(info, progress, progressWindow.Cts.Token)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{version} 已下载并通过校验：{downloadedPath}");
            void Apply()
            {
                if (info.IsSetup)
                {
                    AppLog.Info($"解压版更新：启动安装程序 v{version}；{VelopackUpdateService.DescribeInstall()}");
                    UpdateApplier.ApplySetup(downloadedPath);
                }
                else
                {
                    UpdateApplier.ApplyAndRestart(downloadedPath);
                }
                AppLog.Info($"准备退出当前程序以完成 v{version} 更新");
                Application.Current.Shutdown();
            }
            notifier.PublishReady(version, Apply);

            // The window may have been closed mid-download: the download kept running (closing
            // no longer cancels it), so hand over to the title icon instead of restarting
            // unannounced. Otherwise hold "ready" briefly, then restart.
            if (!progressWindow.IsLoaded)
            {
                AppLog.Info($"更新 v{version} 已下载完成（进度窗口已关闭），等待用户从更新图标重启");
                return;
            }

            progressWindow.Report(new UpdateProgress(UpdateStage.ReadyToRestart, 100, 0, null, null, null));
            await Task.Delay(1200).ConfigureAwait(true);

            progressWindow.Close();
            Apply();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("更新下载已取消");
            notifier.PublishFailed(version, "下载已取消");
            dialogs.Info("已取消本次更新下载。当前版本不受影响，可稍后重试。", "软件更新");
        }
        catch (Exception ex)
        {
            AppLog.Error($"更新到 v{version} 失败", ex);
            notifier.PublishFailed(version, ex.Message);
            if (progressWindow.IsLoaded) progressWindow.Close();
            dialogs.Warn(
                "更新失败，当前版本不受影响。\n\n" +
                $"原因：{ex.Message}\n\n" +
                $"可稍后在设置中重试，或到发布页手动下载：\n{info.ReleasePageUrl}",
                "软件更新");
        }
        finally
        {
            if (progressWindow.IsLoaded) progressWindow.Close();
        }
    }

    /// <summary>
    /// Startup path (legacy channel): download without any window, then let the shell announce
    /// it with the update icon. Every outcome is reflected in that icon — a failed download is
    /// visible (with a retry) and logged with its exception, never swallowed.
    /// </summary>
    private static async Task DownloadSilentlyAsync(UpdateInfo info)
    {
        string version = info.Version.ToString();
        UpdateReadyNotifier.Current.BeginDownloading(version);
        LogPackageCache($"开始下载 v{version} 前");

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
                UpdateReadyNotifier.Current.ReportProgress(ToPercent(p)));
            string downloadedPath = await UpdateService
                .DownloadUpdateAsync(info, progress, CancellationToken.None)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{version} 已在后台下载并通过校验：{downloadedPath}");
            UpdateReadyNotifier.Current.PublishReady(version, () =>
            {
                AppLog.Info($"用户从更新图标确认，应用 v{version}；{VelopackUpdateService.DescribeInstall()}");
                if (info.IsSetup)
                    UpdateApplier.ApplySetup(downloadedPath);
                else
                    UpdateApplier.ApplyAndRestart(downloadedPath);
                Application.Current.Shutdown();
            });
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"后台下载更新 v{version} 已取消");
            UpdateReadyNotifier.Current.PublishFailed(version, "下载已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error($"后台下载更新 v{version} 失败", ex);
            LogPackageCache($"更新 v{version} 下载失败后");
            UpdateReadyNotifier.Current.PublishFailed(version, ex.Message);
        }
    }

    /// <summary>Startup path (Velopack channel): same quiet download against the increment feed.</summary>
    private static async Task DownloadVelopackSilentlyAsync(VelopackCheckResult result)
    {
        string version = result.LatestVersion.ToString();
        UpdateReadyNotifier.Current.BeginDownloading(version);
        LogPackageCache($"开始下载 v{version} 前（增量={result.IsDelta}）");

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
                UpdateReadyNotifier.Current.ReportProgress(ToPercent(p)));
            Velopack.UpdateInfo update = await VelopackService
                .DownloadAsync(result.Update!, progress, CancellationToken.None)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{version} 已在后台下载并通过 Velopack 校验");
            LogPackageCache($"更新 v{version} 下载完成后");
            UpdateReadyNotifier.Current.PublishReady(version, () =>
            {
                AppLog.Info($"用户从更新图标确认，应用 v{version}；{VelopackUpdateService.DescribeInstall()}");
                AppLog.Flush();
                VelopackService.ApplyAndRestart(update);
            });
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"后台下载更新 v{version} 已取消");
            UpdateReadyNotifier.Current.PublishFailed(version, "下载已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error($"后台下载更新 v{version} 失败", ex);
            LogPackageCache($"更新 v{version} 下载失败后");
            UpdateReadyNotifier.Current.PublishFailed(version, ex.Message);
        }
    }

    /// <summary>
    /// Retry after a failed background download (the icon's click action). Same quiet path as
    /// startup: the result returns to the icon, not to a dialog the user has to dismiss.
    /// </summary>
    public static Task RetryAsync(AppSettings settings) =>
        RunCheckAsync(Application.Current?.MainWindow, settings, silent: true);

    private static int? ToPercent(UpdateProgress progress) =>
        progress.Percent is { } percent ? (int)Math.Round(percent) : null;

    /// <summary>
    /// Records the Velopack package cache around a download. The machine that reported "delta
    /// first, full package after the window vanished" had no evidence; the cache listing is what
    /// tells us whether a failed attempt really lost the delta base.
    /// </summary>
    private static void LogPackageCache(string context)
    {
        try
        {
            string? directory = VelopackUpdateService.PackagesDirectory;
            if (directory is null || !Directory.Exists(directory))
            {
                AppLog.Info($"{context}：Velopack 包缓存目录不存在");
                return;
            }

            var entries = Directory.EnumerateFiles(directory)
                .Select(file => $"{Path.GetFileName(file)}（{new FileInfo(file).Length / 1024} KB）")
                .ToList();
            AppLog.Info($"{context}：包缓存={directory}，文件：{(entries.Count == 0 ? "空" : string.Join("、", entries))}，" +
                        $"本地完整包={VelopackUpdateService.HasLocalBasePackage}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{context}：读取包缓存失败（{ex.Message}）");
        }
    }

    /// <summary>
    /// Network trouble (a timeout, a reset connection, GitHub unreachable) is expected on this
    /// machine and must not read as a defect: it lands as WARN with a one-line reason, while a
    /// real failure keeps its stack as ERROR. Support logs then stay triageable.
    /// </summary>
    private static void LogCheckFailure(Exception ex)
    {
        if (IsNetworkFailure(ex))
        {
            AppLog.Warn($"检查更新失败（网络）：{ex.GetType().Name}：{ex.Message}");
            return;
        }

        AppLog.Error("检查更新失败", ex);
    }

    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or IOException
        || (ex.InnerException is not null && IsNetworkFailure(ex.InnerException));

    /// <summary>
    /// Restarts into the downloaded update after one confirmation. Shared by the shell's update
    /// icon and by a manual "check for updates" click made while the package is already waiting.
    /// </summary>
    public static void ApplyReady(IDialogService dialogs)
    {
        if (UpdateReadyNotifier.Current is not { HasUpdate: true, Apply: { } apply }) return;

        string version = UpdateReadyNotifier.Current.Version!;
        bool accepted = dialogs.ConfirmYesNo(
            $"新版本 v{version} 已下载完成，是否立即重启并完成更新？\n\n" +
            "程序会短暂关闭，更新完成后自动重新打开。",
            "软件更新");
        if (!accepted) return;

        try
        {
            apply();
        }
        catch (Exception ex)
        {
            AppLog.Error($"应用更新 v{version} 失败", ex);
            dialogs.Warn(
                "更新失败，当前版本不受影响。\n\n" +
                $"原因：{ex.Message}\n\n" +
                "可稍后在设置中重试，或到发布页手动下载安装最新版。",
                "软件更新");
        }
    }

    // ── Velopack channel (增量推送) — mirrors the legacy flow above against the Velopack feed ──

    private static async Task RunVelopackCheckCoreAsync(Window? owner, IDialogService dialogs, bool silent)
    {
        UpdateProgressWindow? checkingWindow = null;
        if (!silent)
        {
            checkingWindow = new UpdateProgressWindow { Owner = owner };
            checkingWindow.ShowStage(UpdateStage.Checking);
            checkingWindow.Show();
        }

        VelopackCheckResult result;
        try
        {
            result = await VelopackService.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            checkingWindow?.Close();
            LogCheckFailure(ex);
            if (!silent)
                dialogs.Warn(
                    "检查更新时出错，可能是网络不稳定。\n可稍后重试，或到发布页手动下载。\n\n" +
                    $"详情：{ex.Message}",
                    "软件更新");
            return;
        }

        if (!result.UpdateAvailable)
        {
            checkingWindow?.Close();
            AppLog.Info($"当前已是最新版本 v{UpdateService.CurrentVersion}");
            if (!silent)
                dialogs.Info(
                    $"当前已是最新版本（v{UpdateService.CurrentVersion}）。",
                    "软件更新");
            return;
        }

        checkingWindow?.Close();
        AppLog.Info(result.IsDelta
            ? $"发现新版本 v{result.LatestVersion}（当前 v{UpdateService.CurrentVersion}），有增量更新包"
            : $"发现新版本 v{result.LatestVersion}（当前 v{UpdateService.CurrentVersion}），将下载全量更新包");

        if (silent)
        {
            await DownloadVelopackSilentlyAsync(result).ConfigureAwait(true);
            return;
        }

        string sizeText = result.PackageBytes > 0
            ? $"约 {ProgressTextFormatter.FormatBytes(result.PackageBytes)}"
            : "未知大小";
        string packageKind = result.IsDelta
            ? "增量更新包，无需重新下载完整安装包"
            : "全量更新包";
        // A full download on a Velopack install has two very different causes; naming the right
        // one is the difference between a misleading "you are portable" and a one-line fix.
        // Velopack's own .portable marker is authoritative — a Setup install never has it.
        bool portable = VelopackUpdateService.IsPortableInstall;
        string localHint = result.IsDelta
            ? string.Empty
            : portable
                ? "\n\n提示：当前是便携版（解压）运行方式，增量更新不可用，每次都要完整下载。\n" +
                  "改用 Setup.exe 安装一次（同版本覆盖安装即可），之后每次更新只需几 MB 的增量包。"
                : "\n\n提示：安装包缓存里缺少可做差分的完整包（可能被清理工具删除），本次需要完整下载；\n" +
                  "下载完成后新的完整包会自动保留，之后的更新恢复增量。";
        AppLog.Info(
            $"Velopack 本地状态：便携版={portable}，本地完整包={VelopackUpdateService.HasLocalBasePackage}，" +
            $"缓存目录={VelopackUpdateService.PackagesDirectory ?? "无"}");
        bool accepted = dialogs.ConfirmYesNo(
            $"发现新版本 v{result.LatestVersion}（当前 v{UpdateService.CurrentVersion}），是否立即下载并更新？\n\n" +
            $"更新包：{packageKind}，大小 {sizeText}{localHint}\n" +
            "下载、校验均有进度显示，更新完成后程序会自动重启，无需重新安装。",
            "软件更新");

        if (!accepted) return;

        await DownloadAndApplyVelopackAsync(owner, dialogs, result).ConfigureAwait(true);
    }

    private static async Task DownloadAndApplyVelopackAsync(
        Window? owner, IDialogService dialogs, VelopackCheckResult result)
    {
        string version = result.LatestVersion.ToString();
        var progressWindow = new UpdateProgressWindow();
        if (owner is not null) progressWindow.Owner = owner;
        progressWindow.Show();
        progressWindow.Report(new UpdateProgress(
            UpdateStage.Downloading, 0, 0, result.PackageBytes, null,
            result.IsDelta ? "增量更新" : "全量更新"));

        var notifier = UpdateReadyNotifier.Current;
        notifier.BeginDownloading(version);
        LogPackageCache($"开始下载 v{version} 前（增量={result.IsDelta}）");

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (progressWindow.IsLoaded) progressWindow.Report(p);
                notifier.ReportProgress(ToPercent(p));
            });
            Velopack.UpdateInfo update = await VelopackService
                .DownloadAsync(result.Update!, progress, progressWindow.Cts.Token)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{version} 已下载并通过 Velopack 校验");
            LogPackageCache($"更新 v{version} 下载完成后");
            void Apply()
            {
                // Update.exe waits for this process to exit, applies the package and relaunches;
                // the current process terminates inside the call — nothing after it runs.
                AppLog.Info($"准备退出当前程序以完成 v{version} 更新；" +
                            VelopackUpdateService.DescribeInstall());
                AppLog.Flush();
                VelopackService.ApplyAndRestart(update);
            }
            notifier.PublishReady(version, Apply);

            // Closing the progress window no longer cancels the download. If it happened, the
            // package is ready now and the title icon asks for the restart instead of us
            // restarting a user who walked away.
            if (!progressWindow.IsLoaded)
            {
                AppLog.Info($"更新 v{version} 已下载完成（进度窗口已关闭），等待用户从更新图标重启");
                return;
            }

            // Hold the "ready" state briefly — a window that closes and an app that vanishes in
            // the same frame reads as a crash, not a completed update.
            progressWindow.Report(new UpdateProgress(UpdateStage.ReadyToRestart, 100, 0, null, null, null));
            await Task.Delay(1200).ConfigureAwait(true);

            progressWindow.Close();
            Apply();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("更新下载已取消");
            notifier.PublishFailed(version, "下载已取消");
            dialogs.Info("已取消本次更新下载。当前版本不受影响，可稍后重试。", "软件更新");
        }
        catch (Exception ex)
        {
            AppLog.Error($"更新到 v{version} 失败", ex);
            LogPackageCache($"更新 v{version} 下载失败后");
            notifier.PublishFailed(version, ex.Message);
            dialogs.Warn(
                "更新失败，当前版本不受影响。\n\n" +
                $"原因：{ex.Message}\n\n" +
                "可稍后在设置中重试，或到发布页手动下载安装最新版。",
                "软件更新");
        }
        finally
        {
            if (progressWindow.IsLoaded) progressWindow.Close();
        }
    }
}
