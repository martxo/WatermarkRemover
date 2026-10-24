using System.Threading;
using System.Windows;
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
    /// Runs one check. When <paramref name="silent"/> is true (startup path) every negative
    /// outcome — unreachable channel, no update, not configured — stays quiet; only the actual
    /// update flow becomes visible.
    /// </summary>
    public static async Task RunCheckAsync(Window owner, AppSettings settings, bool silent)
    {
        var dialogs = new DialogService(owner);

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
            return;
        }

        try
        {
            await RunCheckCoreAsync(owner, dialogs, silent).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task RunCheckCoreAsync(Window owner, IDialogService dialogs, bool silent)
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
            AppLog.Error("检查更新失败", ex);
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
        bool accepted = dialogs.ConfirmYesNo(
            $"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}），是否立即下载并更新？\n\n" +
            $"更新包大小：{sizeText}\n" +
            "下载、校验均有进度显示，更新完成后程序会自动重启，无需重新安装。",
            "软件更新");

        if (!accepted) return;

        await DownloadAndApplyAsync(owner, dialogs, info).ConfigureAwait(true);
    }

    private static async Task DownloadAndApplyAsync(Window owner, IDialogService dialogs, UpdateInfo info)
    {
        var progressWindow = new UpdateProgressWindow { Owner = owner };
        // This Show() is the whole reason a progress window exists: without it the download runs
        // invisible (regression fixed in v1.8.0).
        progressWindow.Show();
        progressWindow.Report(new UpdateProgress(UpdateStage.Downloading, 0, 0, info.SizeBytes, null, null));

        try
        {
            var progress = new Progress<UpdateProgress>(progressWindow.Report);
            string downloadedPath = await UpdateService
                .DownloadUpdateAsync(info, progress, progressWindow.Cts.Token)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{info.Version} 已下载并通过校验：{downloadedPath}");

            // Hold the "ready" state briefly — a window that closes and an app that vanishes in
            // the same frame reads as a crash, not a completed update.
            progressWindow.Report(new UpdateProgress(UpdateStage.ReadyToRestart, 100, 0, null, null, null));
            await Task.Delay(1200).ConfigureAwait(true);

            progressWindow.Close();
            UpdateApplier.ApplyAndRestart(downloadedPath);
            AppLog.Info($"准备退出当前程序以完成 v{info.Version} 更新");
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("更新下载已取消");
            // The user cancelled deliberately; closing the window is feedback enough.
        }
        catch (Exception ex)
        {
            AppLog.Error($"更新到 v{info.Version} 失败", ex);
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
    /// it with the update icon. Failures stay in the log — a background pass must never open a
    /// dialog over the user's work.
    /// </summary>
    private static async Task DownloadSilentlyAsync(UpdateInfo info)
    {
        try
        {
            var quiet = new Progress<UpdateProgress>(_ => { });
            string downloadedPath = await UpdateService
                .DownloadUpdateAsync(info, quiet, CancellationToken.None)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{info.Version} 已在后台下载并通过校验：{downloadedPath}");
            UpdateReadyNotifier.Current.Publish(info.Version.ToString(), () =>
            {
                AppLog.Info($"用户从更新图标确认，应用 v{info.Version}");
                UpdateApplier.ApplyAndRestart(downloadedPath);
                Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"后台下载更新 v{info.Version} 失败：{ex.Message}");
        }
    }

    /// <summary>Startup path (Velopack channel): same quiet download against the increment feed.</summary>
    private static async Task DownloadVelopackSilentlyAsync(VelopackCheckResult result)
    {
        try
        {
            var quiet = new Progress<UpdateProgress>(_ => { });
            Velopack.UpdateInfo update = await VelopackService
                .DownloadAsync(result.Update!, quiet, CancellationToken.None)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{result.LatestVersion} 已在后台下载并通过 Velopack 校验");
            UpdateReadyNotifier.Current.Publish(result.LatestVersion.ToString(), () =>
            {
                AppLog.Info($"用户从更新图标确认，应用 v{result.LatestVersion}");
                VelopackService.ApplyAndRestart(update);
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"后台下载更新 v{result.LatestVersion} 失败：{ex.Message}");
        }
    }

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

    private static async Task RunVelopackCheckCoreAsync(Window owner, IDialogService dialogs, bool silent)
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
            AppLog.Error("检查更新失败", ex);
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

        await DownloadAndApplyVelopackAsync(dialogs, result).ConfigureAwait(true);
    }

    private static async Task DownloadAndApplyVelopackAsync(IDialogService dialogs, VelopackCheckResult result)
    {
        var progressWindow = new UpdateProgressWindow();
        progressWindow.Show();
        progressWindow.Report(new UpdateProgress(
            UpdateStage.Downloading, 0, 0, result.PackageBytes, null,
            result.IsDelta ? "增量更新" : "全量更新"));

        try
        {
            var progress = new Progress<UpdateProgress>(progressWindow.Report);
            Velopack.UpdateInfo update = await VelopackService
                .DownloadAsync(result.Update!, progress, progressWindow.Cts.Token)
                .ConfigureAwait(true);

            AppLog.Info($"更新 v{update.TargetFullRelease.Version} 已下载并通过 Velopack 校验");

            // Hold the "ready" state briefly — a window that closes and an app that vanishes in
            // the same frame reads as a crash, not a completed update.
            progressWindow.Report(new UpdateProgress(UpdateStage.ReadyToRestart, 100, 0, null, null, null));
            await Task.Delay(1200).ConfigureAwait(true);

            progressWindow.Close();
            // Update.exe waits for this process to exit, applies the package and relaunches;
            // the current process terminates inside the call — nothing after it runs.
            VelopackService.ApplyAndRestart(update);
            AppLog.Info($"准备退出当前程序以完成 v{result.LatestVersion} 更新");
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("更新下载已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error($"更新到 v{result.LatestVersion} 失败", ex);
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
