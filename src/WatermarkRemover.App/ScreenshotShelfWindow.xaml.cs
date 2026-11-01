using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// Tendedero-style screenshot shelf: the newest captures hang on a small top-center strip.
/// A click copies the image, dragging hands the file to another app/folder, double-click opens
/// the original, and 贴图 pins it above other windows. The shelf never edits or deletes files —
/// it is a quick-access surface for the records the screenshot page already owns.
/// </summary>
public partial class ScreenshotShelfWindow : Window
{
    private readonly Action _openFolder;
    private readonly DispatcherTimer _statusTimer;

    private FileJobViewModel? _dragItem;
    private Point _dragStart;
    private bool _dragStarted;

    public ScreenshotShelfWindow(IReadOnlyList<FileJobViewModel> items, Action openFolder)
    {
        _openFolder = openFolder ?? throw new ArgumentNullException(nameof(openFolder));
        InitializeComponent();

        ItemsHost.ItemsSource = items;
        CountText.Text = items.Count == 0 ? string.Empty : $"共 {items.Count} 张";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = string.Empty;
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        Rect work = SystemParameters.WorkArea;
        double left = work.Left + (work.Width - ActualWidth) / 2;
        double maxLeft = work.Right - ActualWidth - 10;
        Left = Math.Clamp(left, work.Left + 10, Math.Max(work.Left + 10, maxLeft));
        Top = work.Top + 24;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => _openFolder();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // --- Thumbnail gestures ----------------------------------------------------

    private void OnThumbnailMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: FileJobViewModel job }) return;

        if (e.ClickCount >= 2)
        {
            job.OpenImageCommand.Execute(null);
            e.Handled = true;
            return;
        }

        _dragItem = job;
        _dragStart = e.GetPosition(this);
        _dragStarted = false;
        if (sender is UIElement element) element.CaptureMouse();
        e.Handled = true;
    }

    private void OnThumbnailMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;

        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        FileJobViewModel job = _dragItem;
        _dragItem = null;
        _dragStarted = true;
        if (sender is UIElement element) element.ReleaseMouseCapture();

        if (job.OutputPath is not { } path || !File.Exists(path))
        {
            _dragStarted = false;
            return;
        }

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDropEffects effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            if (effect != DragDropEffects.None)
            {
                // Like Tendedero: the shot leaves the line once it has been handed over.
                Close();
            }
        }
        catch (Exception ex)
        {
            Logging.AppLog.Warn($"晾衣绳拖出失败：{ex.Message}");
        }
        finally
        {
            _dragStarted = false;
        }

        e.Handled = true;
    }

    private void OnThumbnailMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element) element.ReleaseMouseCapture();

        FileJobViewModel? job = _dragItem;
        _dragItem = null;
        bool dragged = _dragStarted;
        _dragStarted = false;
        if (job is null || dragged) return;

        job.CopyImageCommand.Execute(null);
        StatusText.Text = "已复制到剪贴板";
        _statusTimer.Stop();
        _statusTimer.Start();
        e.Handled = true;
    }
}
