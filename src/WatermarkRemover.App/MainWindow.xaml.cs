using System.IO;
using System.Windows;
using Microsoft.Win32;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Updates;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Jobs.CollectionChanged += (_, _) => SyncListVisibility();
        Loaded += OnWindowLoaded;
    }

    /// <summary>Quiet update check on startup; never disturbs normal work.</summary>
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await UpdateCoordinator.RunCheckAsync(this, _viewModel.Settings, silent: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("启动更新检查意外失败", ex);
        }
    }

    private void SyncListVisibility()
    {
        bool hasJobs = _viewModel.Jobs.Count > 0;
        JobList.Visibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Visibility = hasJobs ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropHintText.Text = "松开鼠标，立即开始去水印";
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropHintText.Text = "只支持 PDF 文件";
        }

        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHintText.Text = "把 PDF 文件拖到这里，松手即开始去水印";

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        string[] pdfs = dropped
            .Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (pdfs.Length == 0)
        {
            MessageBox.Show("只支持 PDF 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _viewModel.AddFilesAsync(pdfs);
    }

    private async void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 PDF 文件",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true) return;
        await _viewModel.AddFilesAsync(dialog.FileNames);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel.Settings) { Owner = this };
        window.ShowDialog();

        // Settings are edited in place on the shared object; persist whatever changed.
        _viewModel.SaveSettingsCommand.Execute(null);
    }

    /// <summary>
    /// Clicking anywhere outside the app (another window, the desktop) deactivates this window —
    /// no WPF mouse event reaches us, so Deactivated is the only reliable "clicked away" signal.
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e) => CommitActiveRenames();

    /// <summary>
    /// Clicking outside the rename box but inside the window commits it. WPF only moves keyboard
    /// focus to focusable elements, so TextBox.LostFocus alone never fires when the user clicks
    /// empty space.
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_viewModel.Jobs.Any(j => j.IsEditing)) return;

        // A click inside the edit box (text area, selection, caret) must not commit.
        if (IsWithinRenameBox(e.OriginalSource as DependencyObject)) return;

        CommitActiveRenames();
    }

    private void CommitActiveRenames()
    {
        foreach (var job in _viewModel.Jobs.Where(j => j.IsEditing).ToList())
            job.CommitEditCommand.Execute(null);
    }

    private static bool IsWithinRenameBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.TextBox) return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>Losing focus commits the inline rename (matches the Enter key).</summary>
    private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { DataContext: FileJobViewModel job })
            job.CommitEditCommand.Execute(null);
    }

    /// <summary>Enter commits the rename; Escape cancels it.</summary>
    private void OnRenameBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { DataContext: FileJobViewModel job }) return;

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            job.CommitEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            job.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
