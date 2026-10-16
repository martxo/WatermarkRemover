using System.Threading;
using System.Windows;
using WatermarkRemover.App.Logging;
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

    /// <summary>1 when a check/update is already running; guards re-entrant triggers.</summary>
    private static int _running;

    /// <summary>
    /// Runs one check. When <paramref name="silent"/> is true (startup path) every negative
    /// outcome — unreachable channel, no update, not configured — stays quiet; only the actual
    /// update flow becomes visible.
    /// </summary>
    public static async Task RunCheckAsync(Window owner, AppSettings settings, bool silent)
    {
        if (!UpdateConfig.IsConfigured)
        {
            AppLog.Info("更新源未配置，跳过检查更新");
            if (!silent)
                MessageBox.Show(owner, "更新源尚未配置（GitHub 仓库地址待填入）。", "软件更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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
            await RunCheckCoreAsync(owner, silent).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task RunCheckCoreAsync(Window owner, bool silent)
    {
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
                MessageBox.Show(owner,
                    "检查更新时出错，可能是网络不稳定。\n可稍后重试，或到发布页手动下载。\n\n" +
                    $"详情：{ex.Message}",
                    "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    MessageBox.Show(owner,
                        $"当前已是最新版本（v{UpdateService.CurrentVersion}）。\n" +
                        $"更新通道最新发布：v{result.ChannelLatestVersion}",
                        "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;

            case UpdateCheckStatus.NoUpdateSource:
                checkingWindow?.Close();
                AppLog.Info($"更新源不可用：{result.Message}");
                if (!silent)
                    MessageBox.Show(owner,
                        $"暂无可用的更新来源。\n\n{result.Message}",
                        "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;

            default: // CheckFailed
                checkingWindow?.Close();
                AppLog.Info($"检查更新失败：{result.Message}");
                if (!silent)
                    MessageBox.Show(owner,
                        "暂时无法连接更新服务器，请检查网络后重试。\n\n" +
                        $"详情：{result.Message}",
                        "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
        }

        UpdateInfo info = result.Update!;
        AppLog.Info($"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}）");

        // The confirmation belongs to the check, not to the download: close the "checking…"
        // window so a modal MessageBox cannot sit on top of a live progress window.
        checkingWindow?.Close();

        string sizeText = info.SizeBytes > 0 ? $"约 {ProgressTextFormatter.FormatBytes(info.SizeBytes)}" : "未知大小";
        MessageBoxResult choice = MessageBox.Show(
            owner,
            $"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}），是否立即下载并更新？\n\n" +
            $"更新包大小：{sizeText}\n" +
            "下载、校验均有进度显示，更新完成后程序会自动重启，无需重新安装。",
            "软件更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (choice != MessageBoxResult.Yes) return;

        await DownloadAndApplyAsync(owner, info).ConfigureAwait(true);
    }

    private static async Task DownloadAndApplyAsync(Window owner, UpdateInfo info)
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
            MessageBox.Show(owner,
                "更新失败，当前版本不受影响。\n\n" +
                $"原因：{ex.Message}\n\n" +
                $"可稍后在设置中重试，或到发布页手动下载：\n{info.ReleasePageUrl}",
                "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (progressWindow.IsLoaded) progressWindow.Close();
        }
    }
}
