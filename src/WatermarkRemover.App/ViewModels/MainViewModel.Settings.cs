using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Presentation;
using WatermarkRemover.Pdf.Rendering;
using WatermarkRemover.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Index;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// True while the settings second-level page covers the whole window. The view switches
    /// between the main shell and the settings page purely from this flag — no new window.
    /// </summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set
        {
            if (SetProperty(ref _isSettingsOpen, value))
            {
                OnPropertyChanged(nameof(IsMainPage));
                // The settings page may have just added the first netdisk account, which is what
                // turns the footer upload button on.
                OnPropertyChanged(nameof(CanUploadBatch));
                OnPropertyChanged(nameof(UploadBatchHint));
            }
        }
    }

    /// <summary>Convenience inverse so the main shell binds with a single bool.</summary>
    public bool IsMainPage => !IsSettingsOpen;

    /// <summary>
    /// Opens the screen-level image viewer for a strip (host-supplied, so the view model never
    /// touches windows). A tile click routes here: full-screen dark veil, the page in the middle,
    /// the film-rail directory on the left — the interaction every desktop image browser uses.
    /// </summary>
    public Action<PdfPreviewViewModel>? PreviewViewerOpener
    {
        get => _preview.ViewerOpener;
        set => _preview.ViewerOpener = value;
    }

    /// <summary>The 网盘 section's state (products, accounts, management window, quick upload).</summary>
    public NetdiskCoordinator Netdisk { get; }

    /// <summary>The settings page view model; non-null only while the page is open.</summary>
    public SettingsViewModel? SettingsPage
    {
        get => _settingsPage;
        private set => SetProperty(ref _settingsPage, value);
    }

    /// <summary>Enters the settings second-level page (fresh working copy each time).</summary>
    public void OpenSettings()
    {
        var page = new SettingsViewModel(Settings, _dialogs, _shell, netdisk: Netdisk.Settings);
        page.RefreshLog();
        SettingsPage = page;
        IsSettingsOpen = true;
        AppLog.Info("打开设置页");
    }

    /// <summary>
    /// Back to the main page: validates the output directory, commits the working copy and
    /// persists. Returns false when validation fails so the page stays open.
    /// </summary>
    public bool SaveAndExitSettings()
    {
        if (SettingsPage is not { } page) { IsSettingsOpen = false; return true; }

        if (page.Settings.OutputMode == OutputMode.CustomDirectory)
        {
            string directory = page.Settings.OutputDirectory.Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                _dialogs.Warn("请填写输出目录，或选择「与源文件相同目录」。", "提示");
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                _dialogs.Warn($"输出目录不可用：{ex.Message}", "提示");
                return false;
            }

            page.Settings.OutputDirectory = directory;
        }

        // Screenshot settings: a malformed gesture would silently register nothing, and a
        // folder that cannot be created would lose every capture. Both are caught here.
        if (!ScreenshotHotkey.TryParse(page.Settings.ScreenshotHotkey, out ScreenshotHotkey hotkey))
        {
            _dialogs.Warn("截图快捷键格式不正确，请填入类似 Ctrl+Alt+A 的组合（至少包含一个修饰键）。", "提示");
            return false;
        }

        page.Settings.ScreenshotHotkey = hotkey.ToString();
        string screenshotDirectory = page.Settings.ScreenshotOutputDirectory?.Trim() ?? string.Empty;
        if (screenshotDirectory.Length != 0)
        {
            try
            {
                Directory.CreateDirectory(screenshotDirectory);
            }
            catch (Exception ex)
            {
                _dialogs.Warn($"截图保存目录不可用：{ex.Message}", "提示");
                return false;
            }
        }

        page.Settings.ScreenshotOutputDirectory = screenshotDirectory;

        page.Commit();
        if (!SaveSettings())
        {
            // Closing now would look like a successful save while the changes die with the
            // process; keep the page open so the user can retry or discard.
            _dialogs.Warn(
                "设置保存失败（配置目录不可写），本次更改在退出后不会保留。请检查目录权限后重试。",
                "保存失败");
            return false;
        }
        CloseSettings();
        return true;
    }

    /// <summary>Back to the main page, dropping every edit made in the working copy.</summary>
    public void DiscardAndExitSettings()
    {
        CloseSettings();
        AppLog.Info("设置页更改已放弃");
    }

    private void CloseSettings()
    {
        IsSettingsOpen = false;
        SettingsPage = null;
    }
    /// <summary>Persists the live settings; false when the file could not be written.</summary>
    private bool SaveSettings()
    {
        // The pipeline preference may have changed: repoint the proxy before persisting so the
        // next job already uses the newly chosen layout block and engine.
        AppOcrEngineChain.ApplyPipeline(Settings.PreferredLayoutStage, Settings.PreferredOcrEngine);

        if (SettingsService.Save(Settings))
        {
            AppLog.Info("设置已保存");
            // Quick suffixes may have changed: refresh the chips of every existing job.
            Board.RefreshSuffixChips(Settings.QuickSuffixes);
            // The gesture / save folder are bound on the screenshot page and the hotkey must be
            // re-registered with the system.
            NotifyScreenshotSettingsChanged();
            return true;
        }

        AppLog.Error("设置保存失败（目录不可写？）");
        return false;
    }
}
