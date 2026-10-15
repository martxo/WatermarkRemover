using System.IO;
using System.Windows;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private List<string> _quickSuffixes;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        SuffixBox.Text = settings.OutputSuffix;
        AutoNameBox.IsChecked = settings.AutoNameByContent;
        RemoveLinksBox.IsChecked = settings.RemoveLinkedAnnotations;
        ConcurrencyBox.SelectedIndex = Math.Clamp(settings.MaxConcurrency, 1, 4) - 1;
        CheckUpdatesBox.IsChecked = settings.CheckUpdatesOnStartup;
        VersionText.Text = $"当前版本：v{UpdateService.CurrentVersion}";

        _quickSuffixes = settings.QuickSuffixes
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        RefreshSuffixList();

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

    private void RefreshSuffixList()
    {
        SuffixList.ItemsSource = null;
        SuffixList.ItemsSource = _quickSuffixes;
    }

    private void OnAddSuffix(object sender, RoutedEventArgs e)
    {
        string token = OutputNameEditor.SanitizeSuffixToken(NewSuffixBox.Text);
        if (token.Length == 0)
        {
            MessageBox.Show("请输入后缀内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_quickSuffixes.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("该后缀已存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _quickSuffixes.Add(token);
        RefreshSuffixList();
        SuffixList.SelectedIndex = _quickSuffixes.Count - 1;
        NewSuffixBox.Text = string.Empty;
        NewSuffixBox.Focus();
    }

    private void OnUpdateSuffix(object sender, RoutedEventArgs e)
    {
        int index = SuffixList.SelectedIndex;
        if (index < 0)
        {
            MessageBox.Show("请先在列表中选中要修改的后缀。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string token = OutputNameEditor.SanitizeSuffixToken(NewSuffixBox.Text);
        if (token.Length == 0)
        {
            MessageBox.Show("请输入新的后缀内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_quickSuffixes.Where((_, i) => i != index).Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("该后缀已存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _quickSuffixes[index] = token;
        RefreshSuffixList();
        SuffixList.SelectedIndex = index;
    }

    private void OnDeleteSuffix(object sender, RoutedEventArgs e)
    {
        int index = SuffixList.SelectedIndex;
        if (index < 0)
        {
            MessageBox.Show("请先在列表中选中要删除的后缀。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _quickSuffixes.RemoveAt(index);
        RefreshSuffixList();
        if (_quickSuffixes.Count > 0)
            SuffixList.SelectedIndex = Math.Min(index, _quickSuffixes.Count - 1);
    }

    private void OnMoveSuffixUp(object sender, RoutedEventArgs e) => MoveSelectedSuffix(-1);

    private void OnMoveSuffixDown(object sender, RoutedEventArgs e) => MoveSelectedSuffix(1);

    /// <summary>Moves the selected suffix by the given offset and keeps it selected.</summary>
    private void MoveSelectedSuffix(int offset)
    {
        int index = SuffixList.SelectedIndex;
        int target = index + offset;
        if (index < 0 || target < 0 || target >= _quickSuffixes.Count)
            return;

        (_quickSuffixes[index], _quickSuffixes[target]) = (_quickSuffixes[target], _quickSuffixes[index]);
        RefreshSuffixList();
        SuffixList.SelectedIndex = target;
    }

    private void OnSuffixBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (SuffixList.SelectedIndex >= 0) OnUpdateSuffix(sender, e);
        else OnAddSuffix(sender, e);
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
        _settings.AutoNameByContent = AutoNameBox.IsChecked == true;
        _settings.RemoveLinkedAnnotations = RemoveLinksBox.IsChecked == true;
        _settings.MaxConcurrency = ConcurrencyBox.SelectedIndex + 1;
        _settings.CheckUpdatesOnStartup = CheckUpdatesBox.IsChecked == true;
        _settings.QuickSuffixes = _quickSuffixes.ToList();

        DialogResult = true;
        Close();
    }
}
