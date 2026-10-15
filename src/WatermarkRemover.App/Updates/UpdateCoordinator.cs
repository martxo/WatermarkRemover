using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// End-to-end update flow shared by the startup check and the manual "check for updates"
/// button: query the channel, prompt, download with progress, swap and restart.
/// </summary>
public static class UpdateCoordinator
{
    private static readonly UpdateService Service = new();

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

        UpdateInfo? info;
        try
        {
            info = await Service.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("检查更新失败", ex);
            if (!silent)
                MessageBox.Show(owner, $"检查更新失败：{ex.Message}", "软件更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (info == null)
        {
            if (!silent)
                MessageBox.Show(owner, $"当前已是最新版本（v{UpdateService.CurrentVersion}）。", "软件更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AppLog.Info($"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}）");

        MessageBoxResult choice = MessageBox.Show(
            owner,
            $"发现新版本 v{info.Version}，是否立即下载并更新？\n\n" +
            $"当前版本：v{UpdateService.CurrentVersion}\n" +
            "更新完成后程序会自动重启，无需重新安装。",
            "软件更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (choice != MessageBoxResult.Yes) return;

        await DownloadAndApplyAsync(owner, info).ConfigureAwait(true);
    }

    private static async Task DownloadAndApplyAsync(Window owner, UpdateInfo info)
    {
        var progressWindow = new UpdateProgressWindow { Owner = owner };
        try
        {
            var progress = new Progress<double>(percent => progressWindow.Report(percent));
            string downloadedPath = await UpdateService.DownloadUpdateAsync(info, progress, progressWindow.Cts.Token);

            progressWindow.Close();
            AppLog.Info($"更新 v{info.Version} 已下载：{downloadedPath}");

            UpdateApplier.ApplyAndRestart(downloadedPath);
            AppLog.Info($"准备退出当前程序以完成 v{info.Version} 更新");
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("更新下载已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error($"更新到 v{info.Version} 失败", ex);
            MessageBox.Show(owner, $"更新失败：{ex.Message}\n\n可稍后在设置中重试，或到发布页手动下载。", "软件更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (progressWindow.IsLoaded) progressWindow.Close();
        }
    }
}
