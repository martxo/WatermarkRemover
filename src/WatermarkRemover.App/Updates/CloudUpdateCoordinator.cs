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
    public static async Task<bool> TryRunAsync(
        Window? owner, IDialogService dialogs, AppSettings settings, bool silent)
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
            case CloudCheckStatus.Failed:
                AppLog.Info($"云更新通道不可用（{check.Status}）：{check.Message}，改走 GitHub 兜底");
                return false;

            case CloudCheckStatus.NotConfigured:
                return false;

            case CloudCheckStatus.UpToDate:
                AppLog.Info(check.Message);
                if (!silent) dialogs.Info(check.Message + "\n\n（阿里云盘为默认更新通道，GitHub 安装包保留作兜底。）", "软件更新");
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
            await DownloadSilentlyAsync(service, check, version).ConfigureAwait(true);
            return true;
        }

        bool accepted = dialogs.ConfirmYesNo(
            $"云端发现新版本 v{version}（当前 v{check.Current}），是否立即更新？\n\n" +
            $"{detail}\n\n" +
            "更新会短暂关闭程序，替换文件后自动重新打开；失败会自动还原到当前版本。",
            "软件更新");
        if (!accepted) return true;

        await DownloadWithWindowAsync(owner, dialogs, service, check, version).ConfigureAwait(true);
        return true;
    }

    /// <summary>Startup path: quiet download, then the title-bar icon offers the restart.</summary>
    private static async Task DownloadSilentlyAsync(
        CloudUpdateService service, CloudCheckResult check, string version)
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
                AppLog.Flush();
                service.ApplyAndRestart(staged, "update", $"更新到 v{version}");
                Application.Current.Shutdown();
            });
            AppLog.Info($"云更新 v{version} 已在后台下载完成，等待用户重启");
        }
        catch (Exception ex)
        {
            AppLog.Error($"云更新 v{version} 后台下载失败", ex);
            notifier.PublishFailed(version, ex.Message);
        }
    }

    /// <summary>Manual path: visible progress window, then replace files and restart.</summary>
    private static async Task DownloadWithWindowAsync(
        Window? owner, IDialogService dialogs, CloudUpdateService service,
        CloudCheckResult check, string version)
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

            AppLog.Info($"准备退出程序以应用云更新 v{version}");
            AppLog.Flush();
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
