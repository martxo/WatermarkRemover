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
    /// <summary>The last idle hotkey message; restored after a capture's transient busy line.</summary>
    private string _idleHotkeyState = "正在注册全局快捷键…";

    /// <summary>
    /// The shell supplies the overlay runner (windows are a view concern); the view model only
    /// decides what a result means: write a record, report errors and keep the page text fresh.
    /// </summary>
    public Func<Task<ScreenshotCaptureResult?>>? ScreenshotCaptureHandler { get; set; }

    /// <summary>Full-desktop capture; the int is the countdown in seconds (0 = immediate).</summary>
    public Func<int, Task<ScreenshotCaptureResult?>>? ScreenshotFullScreenHandler { get; set; }

    /// <summary>
    /// Settings-page preflight: returns an error for a gesture that cannot be registered, or
    /// null when it is free. Supplied by the shell (it owns the hotkey registration).
    /// </summary>
    public Func<ScreenshotHotkey, string?>? ScreenshotHotkeyValidator { get; set; }

    /// <summary>Raised after settings are saved so the shell can re-register the global hotkey.</summary>
    public event EventHandler? ScreenshotHotkeyChanged;

    public RelayCommand CaptureScreenshotCommand { get; }

    public RelayCommand CaptureFullScreenCommand { get; }

    public RelayCommand CaptureDelayedFullScreenCommand { get; }

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

    /// <summary>
    /// Called by the shell after each (re-)registration attempt. <paramref name="fallback"/> is
    /// the previous working gesture when a new one could not be registered — the page must say
    /// which shortcut is actually live instead of leaving the user guessing.
    /// </summary>
    public void ReportScreenshotHotkey(
        ScreenshotHotkey requested, bool registered, string? error, ScreenshotHotkey? fallback = null)
    {
        string message = registered
            ? $"全局快捷键 {requested} 已生效，软件在后台时也能截图"
            : fallback is { } previous
                ? $"新快捷键 {requested} 未能生效：{error ?? "可能已被其他程序占用"}；仍使用 {previous}"
                : $"快捷键 {requested} 未能注册：{error ?? "可能已被其他程序占用"}；仍可点「开始截图」";
        _idleHotkeyState = message;
        ScreenshotStatusText = message;
    }

    /// <summary>Binding text follows the saved settings (called after a successful save).</summary>
    private void NotifyScreenshotSettingsChanged()
    {
        OnPropertyChanged(nameof(ScreenshotHotkeyText));
        OnPropertyChanged(nameof(ScreenshotDirectoryText));
        ScreenshotHotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task CaptureScreenshotAsync() => RunScreenshotAsync(
        ScreenshotCaptureHandler, "正在截图…", "截图");

    public Task CaptureFullScreenAsync(int delaySeconds) => RunScreenshotAsync(
        ScreenshotFullScreenHandler is null ? null : () => ScreenshotFullScreenHandler(delaySeconds),
        delaySeconds > 0 ? $"{delaySeconds} 秒后全屏截图…" : "正在全屏截图…",
        delaySeconds > 0 ? "延时截图" : "全屏截图");

    /// <summary>One gate/lifecycle for every capture entry point (button, hotkey, full screen).</summary>
    private async Task RunScreenshotAsync(
        Func<Task<ScreenshotCaptureResult?>>? runner, string busyText, string dialogTitle)
    {
        if (_screenshotBusy) return;

        if (runner is null)
        {
            _dialogs.Warn("截图界面尚未就绪，请稍后再试。", dialogTitle);
            return;
        }

        _screenshotBusy = true;
        RaiseScreenshotCommandStates();
        if (busyText.Length > 0) ScreenshotStatusText = busyText;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ScreenshotCaptureResult? result = await runner().ConfigureAwait(true);
            if (result is null || result.Canceled) return;

            if (result.SavedPath is { } path)
                AddScreenshotJob(path, result);

            if (result.Error is { Length: > 0 } error)
            {
                string detail = result.SavedPath is { } saved
                    ? $"{error}\n\n图片已保存到：{saved}"
                    : $"{error}\n\n本次截图没有生成文件。";
                _dialogs.Warn(detail, dialogTitle);
            }

            AppLog.Info($"截图完成：{result.Width}×{result.Height}，复制{(result.Copied ? "成功" : "失败")}，" +
                        $"耗时 {stopwatch.ElapsedMilliseconds} ms" +
                        (result.SavedPath is { } output ? $"，文件 {output}" : string.Empty));
        }
        catch (Exception ex)
        {
            AppLog.Error("截图流程异常", ex);
            _dialogs.Warn($"截图失败：{ex.Message}", dialogTitle);
        }
        finally
        {
            _screenshotBusy = false;
            ScreenshotStatusText = _idleHotkeyState;
            RaiseScreenshotCommandStates();
        }
    }

    private void RaiseScreenshotCommandStates()
    {
        CaptureScreenshotCommand.RaiseCanExecuteChanged();
        CaptureFullScreenCommand.RaiseCanExecuteChanged();
        CaptureDelayedFullScreenCommand.RaiseCanExecuteChanged();
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
