using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class MainViewModel
{
    // --- 截图 ---------------------------------------------------------------

    /// <summary>Only one capture overlay at a time: a second trigger (hotkey + button) is ignored.</summary>
    private bool _screenshotBusy;

    private string _screenshotHotkeyState = "正在注册全局快捷键…";

    /// <summary>
    /// The shell supplies the overlay runner (windows are a view concern); the view model only
    /// decides what a result means: write a record, report errors and keep the page text fresh.
    /// </summary>
    public Func<Task<ScreenshotCaptureResult?>>? ScreenshotCaptureHandler { get; set; }

    /// <summary>Raised after settings are saved so the shell can re-register the global hotkey.</summary>
    public event EventHandler? ScreenshotHotkeyChanged;

    public RelayCommand CaptureScreenshotCommand { get; }

    public RelayCommand OpenScreenshotFolderCommand { get; }

    /// <summary>Canonical gesture text (saved value, or the default when it is malformed).</summary>
    public string ScreenshotHotkeyText => ScreenshotHotkey.ParseOrDefault(Settings.ScreenshotHotkey).ToString();

    /// <summary>The folder screenshots actually land in (custom setting or 图片\Screenshots).</summary>
    public string ScreenshotDirectoryText => ScreenshotNaming.ResolveDirectory(Settings);

    /// <summary>Whether the global shortcut is live — the page must say so when another app owns it.</summary>
    public string ScreenshotStatusText
    {
        get => _screenshotHotkeyState;
        private set => SetProperty(ref _screenshotHotkeyState, value);
    }

    /// <summary>Called by the shell after each (re-)registration attempt.</summary>
    public void ReportScreenshotHotkey(bool registered, string? error)
    {
        ScreenshotStatusText = registered
            ? $"全局快捷键 {ScreenshotHotkeyText} 已生效，软件在后台时也能截图"
            : $"快捷键 {ScreenshotHotkeyText} 未能注册：{error ?? "可能已被其他程序占用"}；仍可点「开始截图」";
    }

    /// <summary>Binding text follows the saved settings (called after a successful save).</summary>
    private void NotifyScreenshotSettingsChanged()
    {
        OnPropertyChanged(nameof(ScreenshotHotkeyText));
        OnPropertyChanged(nameof(ScreenshotDirectoryText));
        ScreenshotHotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CaptureScreenshotAsync()
    {
        if (_screenshotBusy) return;

        if (ScreenshotCaptureHandler is null)
        {
            _dialogs.Warn("截图界面尚未就绪，请稍后再试。", "截图");
            return;
        }

        _screenshotBusy = true;
        CaptureScreenshotCommand.RaiseCanExecuteChanged();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ScreenshotCaptureResult? result = await ScreenshotCaptureHandler().ConfigureAwait(true);
            if (result is null || result.Canceled) return;

            if (result.SavedPath is { } path)
                AddScreenshotJob(path, result);

            if (result.Error is { Length: > 0 } error)
            {
                string detail = result.SavedPath is { } saved
                    ? $"{error}\n\n图片已保存到：{saved}"
                    : $"{error}\n\n本次截图没有生成文件。";
                _dialogs.Warn(detail, "截图");
            }

            AppLog.Info($"截图完成：{result.Width}×{result.Height}，复制{(result.Copied ? "成功" : "失败")}，" +
                        $"耗时 {stopwatch.ElapsedMilliseconds} ms" +
                        (result.SavedPath is { } output ? $"，文件 {output}" : string.Empty));
        }
        catch (Exception ex)
        {
            AppLog.Error("截图流程异常", ex);
            _dialogs.Warn($"截图失败：{ex.Message}", "截图");
        }
        finally
        {
            _screenshotBusy = false;
            CaptureScreenshotCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>A saved screenshot joins the page's history like any other finished file, so
    /// rename / open-folder / delete / upload all work without a second code path.</summary>
    private void AddScreenshotJob(string path, ScreenshotCaptureResult result)
    {
        var job = new FileJobViewModel(path, Settings.QuickSuffixes)
        {
            OwnerFeatureId = "screenshot.page",
        };
        WireJob(job);
        job.Apply(FeatureResult.Success(
            path, path,
            $"截图 {result.Width}×{result.Height}" + (result.Copied ? " · 已复制" : string.Empty),
            1));
        Board.Add(job);
        // Screenshots have no queue behind them: one capture = one finished record.
        Board.MarkCompleted(job);
    }

    private void OpenScreenshotFolder()
    {
        string directory = ScreenshotNaming.ResolveDirectory(Settings);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"截图目录不可用：{ex.Message}", "截图");
            return;
        }

        if (!_shell.TryOpenDirectory(directory, out string? error))
            _dialogs.Warn($"无法打开截图目录：{error ?? directory}", "截图");
    }
}
