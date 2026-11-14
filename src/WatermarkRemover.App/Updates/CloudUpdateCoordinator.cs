using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Runs the 阿里云盘 channel through the same progress/restart surface as the other channels.
/// It is tried first: when a cloud account exists the cloud repository is the source of truth;
/// when it is missing or unreachable the caller falls back to Velopack/GitHub.
/// </summary>
public static class CloudUpdateCoordinator
{
    /// <summary>Returns true when the cloud channel handled the check (including "up to date").</summary>
    /// <param name="onDownloadFailed">
    /// Invoked when the cloud check found an update but the download died under the user (the
    /// CDN keeps throttling a shared egress IP, 出差外地). The caller passes its GitHub/Velopack
    /// fallback so one click still ends in an updated app instead of an error dialog.
    /// </param>
    public static async Task<bool> TryRunAsync(
        Window? owner, IDialogService dialogs, AppSettings settings, bool silent,
        Func<Task>? onDownloadFailed = null)
    {
        var service = new CloudUpdateService(settings, new DpapiSecretProtector());
        if (!service.IsConfigured)
        {
            return false;
        }

        CloudCheckResult check;
        try
        {
            check = await service.CheckAsync(UpdateService.CurrentVersion).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("云更新检查失败", ex);
            return false;
        }

        switch (check.Status)
        {
            case CloudCheckStatus.NoRelease:
                AppLog.Info($"云更新通道不可用（{check.Status}）：{check.Message}，改走 GitHub 兜底");
                return false;

            case CloudCheckStatus.Failed:
                AppLog.Info($"云更新通道不可用（{check.Status}）：{check.Message}，改走 GitHub 兜底");
                if (!silent && (check.Message.Contains("令牌", StringComparison.Ordinal) ||
                                check.Message.Contains("授权", StringComparison.Ordinal) ||
                                check.Message.Contains("登录", StringComparison.Ordinal)))
                {
                    dialogs.Warn(
                        "阿里云盘登录已失效，本次改用 GitHub 兜底更新。\n\n" +
                        "请到「设置 → 网盘设置」重新扫码登录一次，之后的更新会恢复走阿里云盘增量。",
                        "软件更新");
                }

                return false;

            case CloudCheckStatus.NotConfigured:
                return false;

            case CloudCheckStatus.UpToDate:
                AppLog.Info(check.Message);
                if (!silent) dialogs.Info(check.Message + "\n\n（更新镜像优先，镜像不可用时自动回退阿里云盘，GitHub 安装包保留作最终兜底。）", "软件更新");
                return true;

            case CloudCheckStatus.UpdateAvailable:
                break;
        }

        if (check.Plan is null || !check.Plan.HasChanges || check.Latest is null)
        {
            AppLog.Info(check.Message);
            if (!silent) dialogs.Info(check.Message, "软件更新");
            return true;
        }

        string version = check.Latest.ToString();
        string detail = $"需要下载 {check.Plan.ChangedFiles.Count} 个文件，" +
                        $"仅 {CloudUpdateService.FormatBytes(check.Plan.DownloadBytes)}" +
                        $"（只取变更分块，不再下载完整包）";

        if (silent)
        {
            await DownloadSilentlyAsync(service, check, version, onDownloadFailed).ConfigureAwait(true);
            return true;
        }

        bool accepted = dialogs.ConfirmYesNo(
            $"云端发现新版本 v{version}（当前 v{check.Current}），是否立即更新？\n\n" +
            $"{detail}\n\n" +
            "更新会短暂关闭程序，替换文件后自动重新打开；失败会自动还原到当前版本。",
            "软件更新");
        if (!accepted) return true;

        await DownloadWithWindowAsync(owner, dialogs, service, check, version, onDownloadFailed).ConfigureAwait(true);
        return true;
    }

    /// <summary>Startup path: quiet download, then the title-bar icon offers the restart.</summary>
    private static async Task DownloadSilentlyAsync(
        CloudUpdateService service, CloudCheckResult check, string version,
        Func<Task>? onDownloadFailed)
    {
        var notifier = UpdateReadyNotifier.Current;
        notifier.BeginDownloading(version);
        try
        {
            var progress = new Progress<CloudUpdateProgress>(p =>
                notifier.ReportProgress(p.Percent is double percent ? (int)Math.Round(percent) : null));
            CloudStageResult staged = await service
                .StageAsync(check, progress, CancellationToken.None)
                .ConfigureAwait(true);

            notifier.PublishReady(version, () =>
            {
                AppLog.Info($"用户从更新图标确认，应用云更新 v{version}");
                App.NoteExitReason($"用户确认云更新到 v{version}");
                service.ApplyAndRestart(staged, "update", $"更新到 v{version}");
                Application.Current.Shutdown();
            });
            AppLog.Info($"云更新 v{version} 已在后台下载完成，等待用户重启");
        }
        catch (Exception ex)
        {
            AppLog.Error($"云更新 v{version} 后台下载失败", ex);
            if (onDownloadFailed is null)
            {
                notifier.PublishFailed(version, ex.Message);
                return;
            }

            // The cloud channel died on a network it cannot fix (shared-IP throttle). Switch
            // to GitHub inside the same session: the notifier is re-armed by that channel's
            // own BeginDownloading, so the user still ends up with a restart prompt.
            AppLog.Info("云盘下载失败，自动改走 GitHub 兜底通道");
            try
            {
                await onDownloadFailed().ConfigureAwait(true);
            }
            catch (Exception fallbackEx)
            {
                AppLog.Error("GitHub 兜底通道下载失败", fallbackEx);
                notifier.PublishFailed(version, fallbackEx.Message);
            }
        }
    }

    /// <summary>Manual path: visible progress window, then replace files and restart.</summary>
    private static async Task DownloadWithWindowAsync(
        Window? owner, IDialogService dialogs, CloudUpdateService service,
        CloudCheckResult check, string version, Func<Task>? onDownloadFailed)
    {
        var progressWindow = new UpdateProgressWindow();
        if (owner is not null) progressWindow.Owner = owner;
        progressWindow.Show();
        progressWindow.Report(new UpdateProgress(UpdateStage.Downloading, 0, 0,
            check.Plan?.DownloadBytes, null, "阿里云盘"));

        try
        {
            var progress = new Progress<CloudUpdateProgress>(p => progressWindow.Report(Map(p)));
            CloudStageResult staged = await service
                .StageAsync(check, progress, progressWindow.Cts.Token)
                .ConfigureAwait(true);

            progressWindow.Report(new UpdateProgress(UpdateStage.ReadyToRestart, 100, 0, null, null, null));
            await Task.Delay(1000).ConfigureAwait(true);
            progressWindow.Close();

            App.NoteExitReason($"应用云更新到 v{version}");
            service.ApplyAndRestart(staged, "update", $"更新到 v{version}");
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"云更新 v{version} 已取消");
            dialogs.Info("已取消本次云更新，当前版本不受影响。", "软件更新");
            progressWindow.Close();
        }
        catch (Exception ex)
        {
            AppLog.Error($"云更新 v{version} 失败", ex);
            if (progressWindow.IsLoaded) progressWindow.Close();

            if (onDownloadFailed is not null)
            {
                // Same one-click promise: the cloud channel lost the network, GitHub takes
                // over with its own progress window instead of a dead-end error dialog.
                AppLog.Info("云盘下载失败，自动改走 GitHub 兜底通道");
                try
                {
                    await onDownloadFailed().ConfigureAwait(true);
                    return;
                }
                catch (Exception fallbackEx)
                {
                    AppLog.Error("GitHub 兜底通道下载失败", fallbackEx);
                    dialogs.Warn(
                        $"云更新失败，GitHub 兜底通道也未成功，当前版本不受影响。\n\n" +
                        $"云盘原因：{ex.Message}\n兜底原因：{fallbackEx.Message}\n\n" +
                        "可稍后重试，或换一个网络环境再试。",
                        "软件更新");
                    return;
                }
            }

            dialogs.Warn(
                $"云更新失败，当前版本不受影响。\n\n原因：{ex.Message}\n\n" +
                "可稍后重试，或到「软件更新」使用 GitHub 安装包兜底。",
                "软件更新");
        }
    }

    private static UpdateProgress Map(CloudUpdateProgress progress)
    {
        UpdateStage stage = progress.Stage switch
        {
            CloudUpdateStage.Downloading => UpdateStage.Downloading,
            CloudUpdateStage.Assembling => UpdateStage.Verifying,
            CloudUpdateStage.Ready => UpdateStage.ReadyToRestart,
            _ => UpdateStage.Checking,
        };
        return new UpdateProgress(stage, progress.Percent, progress.Bytes, progress.TotalBytes, null, "阿里云盘");
    }
}
