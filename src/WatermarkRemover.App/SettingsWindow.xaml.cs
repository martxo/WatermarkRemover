using System.IO;
using System.Windows;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        SuffixBox.Text = settings.OutputSuffix;
        RemoveLinksBox.IsChecked = settings.RemoveLinkedAnnotations;
        ConcurrencyBox.SelectedIndex = Math.Clamp(settings.MaxConcurrency, 1, 4) - 1;
        CheckUpdatesBox.IsChecked = settings.CheckUpdatesOnStartup;
        VersionText.Text = $"当前版本：v{UpdateService.CurrentVersion}";

        if (settings.OutputMode == OutputMode.CustomDirectory && !string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            CustomDirRadio.IsChecked = true;
            DirBox.Text = settings.OutputDirectory;
        }
        else
        {
            SameDirRadio.IsChecked = true;
        }

        UpdateSuffixPreview();
    }

    private void UpdateSuffixPreview()
    {
        string suffix = OutputNameGenerator.SanitizeSuffix(SuffixBox.Text);
        SuffixPreview.Text = $"示例：扫描件.pdf → 扫描件{suffix}.pdf";
    }

    private void OnOutputModeChanged(object sender, RoutedEventArgs e)
    {
        if (DirBox == null) return;
        DirBox.IsEnabled = CustomDirRadio.IsChecked == true;
    }

    private void OnDirChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Handled on save; nothing to do live.
    }

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择输出目录",
        };

        if (!string.IsNullOrWhiteSpace(DirBox.Text) && Directory.Exists(DirBox.Text))
            dialog.InitialDirectory = DirBox.Text;

        if (dialog.ShowDialog(this) == true)
        {
            DirBox.Text = dialog.FolderName;
            CustomDirRadio.IsChecked = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            await UpdateCoordinator.RunCheckAsync(this, _settings, silent: false);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string suffix = OutputNameGenerator.SanitizeSuffix(SuffixBox.Text);
        bool customMode = CustomDirRadio.IsChecked == true;
        string directory = DirBox.Text.Trim();

        if (customMode)
        {
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
        }

        _settings.OutputSuffix = suffix;
        _settings.OutputMode = customMode ? OutputMode.CustomDirectory : OutputMode.SameDirectory;
        _settings.OutputDirectory = directory;
        _settings.RemoveLinkedAnnotations = RemoveLinksBox.IsChecked == true;
        _settings.MaxConcurrency = ConcurrencyBox.SelectedIndex + 1;
        _settings.CheckUpdatesOnStartup = CheckUpdatesBox.IsChecked == true;

        DialogResult = true;
        Close();
    }
}
