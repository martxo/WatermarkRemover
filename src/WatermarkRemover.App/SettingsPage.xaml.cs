using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WatermarkRemover.App.Updates;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// Settings second-level page: the whole window switches to this view instead of opening a
/// dialog. Only view concerns live here — folder/file pickers, the manual update check (which
/// needs a window as dialog owner) and the back/discard navigation. Every piece of state lives
/// in <see cref="SettingsViewModel"/> (working copy) / <see cref="MainViewModel"/> (page state).
/// Leaving via "返回应用" validates + commits + persists; "放弃更改" drops the working copy.
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    /// <summary>Keeps the log-export event wired to whichever working copy is bound.</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel old) old.ExportRequested -= OnExportRequested;
        if (e.NewValue is SettingsViewModel fresh)
        {
            fresh.ExportRequested += OnExportRequested;
            SyncScanColorRadios(fresh);
        }
    }

    /// <summary>Restores the checked colour radio from the working copy (each visit gets a fresh
    /// copy, so the strip must re-read it — a discarded edit must not stick visually).</summary>
    private void SyncScanColorRadios(SettingsViewModel vm)
    {
        int color = Math.Clamp(vm.Settings.ScanColor, 0, 3);
        RadioButton radio = color switch
        {
            0 => ScanColorAutoBox,
            1 => ScanColorColorBox,
            2 => ScanColorGrayBox,
            _ => ScanColorBlackWhiteBox,
        };
        radio.IsChecked = true;
    }

    /// <summary>Writes the checked colour into the working copy; persisted by "返回应用"
    /// (Commit), matching every other setting on this page.</summary>
    private void OnScanColorChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is RadioButton radio &&
            int.TryParse(radio.Tag as string, out int color))
            vm.Settings.ScanColor = color;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Main?.SaveAndExitSettings();

    private void OnDiscardClick(object sender, RoutedEventArgs e) => Main?.DiscardAndExitSettings();

    private void OnSuffixTextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel?.NotifySuffixChanged();

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择输出目录" };

        if (!string.IsNullOrWhiteSpace(DirBox.Text) && Directory.Exists(DirBox.Text))
            dialog.InitialDirectory = DirBox.Text;

        if (Window.GetWindow(this) is not { } window || dialog.ShowDialog(window) != true) return;

        DirBox.Text = dialog.FolderName;
        // Picking a folder implies the custom-directory mode.
        if (ViewModel is { } vm) vm.UseCustomDirectory = true;
    }

    /// <summary>Scan output directory: pick a folder into the scan settings' working copy.</summary>
    private void OnBrowseScanDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择扫描输出目录" };

        if (!string.IsNullOrWhiteSpace(ScanDirBox.Text) && Directory.Exists(ScanDirBox.Text))
            dialog.InitialDirectory = ScanDirBox.Text;

        if (Window.GetWindow(this) is not { } window || dialog.ShowDialog(window) != true) return;

        ScanDirBox.Text = dialog.FolderName;
    }

    /// <summary>Scan output directory: empty = save to the Pictures folder (the default).</summary>
    private void OnResetScanDirectory(object sender, RoutedEventArgs e) => ScanDirBox.Text = string.Empty;

    private void OnBrowseTransferDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择手机传文件保存目录" };

        if (!string.IsNullOrWhiteSpace(TransferDirBox.Text) && Directory.Exists(TransferDirBox.Text))
            dialog.InitialDirectory = TransferDirBox.Text;

        if (Window.GetWindow(this) is not { } window || dialog.ShowDialog(window) != true) return;

        TransferDirBox.Text = dialog.FolderName;
    }

    /// <summary>Phone-transfer directory: empty = the Desktop itself (the default).</summary>
    private void OnResetTransferDirectory(object sender, RoutedEventArgs e) => TransferDirBox.Text = string.Empty;

    /// <summary>Enter in the new-suffix box: replace the selection or add a new suffix.</summary>
    private void OnSuffixBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (ViewModel is not { } vm) return;
        if (vm.SelectedSuffixIndex >= 0) vm.UpdateSuffixCommand.Execute(null);
        else vm.AddSuffixCommand.Execute(null);
    }

    private void OnExportRequested(object? sender, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出日志文件",
            Filter = "日志文件|*.txt",
            FileName = suggestedFileName,
        };

        if (Window.GetWindow(this) is { } window &&
            dialog.ShowDialog(window) == true &&
            ViewModel is { } vm)
            vm.ExportLogTo(dialog.FileName);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || Window.GetWindow(this) is not { } window) return;

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";
        try
        {
            await UpdateCoordinator.RunCheckAsync(window, vm.Settings, silent: false);
        }
        finally
        {
            CheckUpdateButton.Content = "检查更新";
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
