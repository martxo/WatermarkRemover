using System.IO;
using System.Windows;
using Microsoft.Win32;
using WatermarkRemover.App.Updates;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App;

/// <summary>
/// Thin binding shell over <see cref="SettingsViewModel"/>: only view concerns stay here —
/// file/folder pickers, the output-directory validation on save, and the manual update check
/// (which needs the window as dialog owner). All settings state and commands live in the VM.
/// The shared <see cref="AppSettings"/> object is only touched on save; cancel discards edits.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _viewModel = new SettingsViewModel(settings);
        DataContext = _viewModel;
        _viewModel.ExportRequested += OnExportRequested;

        VersionText.Text = $"当前版本：v{UpdateService.CurrentVersion}";

        if (settings.OutputMode == OutputMode.CustomDirectory && !string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            CustomDirRadio.IsChecked = true;
            DirBox.IsEnabled = true;
        }
        else
        {
            SameDirRadio.IsChecked = true;
        }

        _viewModel.RefreshLog();
    }

    private void OnSuffixTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        _viewModel.NotifySuffixChanged();

    private void OnOutputModeChanged(object sender, RoutedEventArgs e)
    {
        if (DirBox == null) return; // not built yet during InitializeComponent
        DirBox.IsEnabled = CustomDirRadio.IsChecked == true;
    }

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择输出目录" };

        if (!string.IsNullOrWhiteSpace(DirBox.Text) && Directory.Exists(DirBox.Text))
            dialog.InitialDirectory = DirBox.Text;

        if (dialog.ShowDialog(this) == true)
        {
            DirBox.Text = dialog.FolderName;
            CustomDirRadio.IsChecked = true;
        }
    }

    /// <summary>Enter in the new-suffix box: replace the selection or add a new suffix.</summary>
    private void OnSuffixBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;

        if (_viewModel.SelectedSuffixIndex >= 0)
            _viewModel.UpdateSuffixCommand.Execute(null);
        else
            _viewModel.AddSuffixCommand.Execute(null);
    }

    private void OnExportRequested(object? sender, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出日志文件",
            Filter = "日志文件|*.txt",
            FileName = suggestedFileName,
        };

        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportLogTo(dialog.FileName);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";
        try
        {
            await UpdateCoordinator.RunCheckAsync(this, _settings, silent: false);
        }
        finally
        {
            CheckUpdateButton.Content = "检查更新";
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // The output-mode radios are plain view state; fold them into the working copy here.
        bool customMode = CustomDirRadio.IsChecked == true;
        _viewModel.Settings.OutputMode = customMode ? OutputMode.CustomDirectory : OutputMode.SameDirectory;

        if (customMode)
        {
            string directory = _viewModel.Settings.OutputDirectory.Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                MessageBox.Show("请填写输出目录，或选择「与源文件相同目录」。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"输出目录不可用：{ex.Message}", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _viewModel.Settings.OutputDirectory = directory;
        }

        _viewModel.Commit();
        DialogResult = true;
        Close();
    }
}
