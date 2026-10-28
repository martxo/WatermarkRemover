using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Updates;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

public partial class MainWindow : Window
{
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE — Windows 11 attribute that rounds the window.</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND — the standard (8px) window corner, same as reference-style apps.</summary>
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly MainViewModel _viewModel = new();
    private readonly Services.IDialogService _dialogs = new Services.DialogService();

    /// <summary>
    /// Drag-exit watchdog. DragLeave is unreliable in WPF: it bubbles from every child element,
    /// and the leave caused by dragging out of the window is often swallowed entirely. Instead
    /// of trusting it, every drag heartbeat (DragEnter/DragOver) re-arms this timer; if no
    /// heartbeat arrives the cursor position is verified before resetting the drop zone.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _dragWatchdog;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // Tile clicks open the screen-level viewer (dark veil over the whole screen, film rail
        // on the left) — a real window, so the host wires it here, outside the view model.
        _viewModel.PreviewViewerOpener = strip => FullscreenViewerWindow.ShowFor(strip);
        _dragWatchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _dragWatchdog.Tick += OnDragWatchdogTick;
        _viewModel.Board.Jobs.CollectionChanged += (_, _) => SyncListVisibility();
        // Section switches don't touch the Jobs collection — follow the count property instead.
        _viewModel.Board.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobBoard.VisibleJobCount)) SyncListVisibility();
        };
        Loaded += OnWindowLoaded;
    }

    /// <summary>
    /// The inline thumbnail strip scrolls inside its record card; the record list itself
    /// scrolls the page. WPF's inner ScrollViewer swallows the wheel even when it has
    /// reached its end, so the page froze exactly when the user wanted to reach the next
    /// record. Once the strip has nothing left to show, hand the wheel over to the list.
    /// Both direction checks matter: "up" must test headroom ABOVE (offset &gt; 0) and
    /// "down" headroom BELOW — the previous code had them swapped, so a strip scrolled to
    /// its top ate every upward notch (they clamped at offset 0) and the page never moved;
    /// and the forwarded step must be negated because wheel-up arrives as a POSITIVE delta.
    /// </summary>
    private void OnStripPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer strip) return;
        int direction = Math.Sign(e.Delta);
        if (direction == 0) return;

        // The strip still has room in this direction — it takes the wheel itself.
        bool canScroll = direction > 0
            ? strip.VerticalOffset > 0
            : strip.VerticalOffset < strip.ScrollableHeight - 0.5;
        if (canScroll) return;

        ScrollViewer? list = FindAncestorScrollViewer(strip);
        if (list is null) return;

        // One wheel notch ≈ three lines, the same distance the list itself would scroll.
        // Delta is positive scrolling up, so the step along the list must be negated.
        double step = -e.Delta / 120.0 * 48.0;
        list.ScrollToVerticalOffset(Math.Clamp(list.VerticalOffset + step, 0, list.ScrollableHeight));
        e.Handled = true;
    }

    /// <summary>The nearest ScrollViewer above <paramref name="source"/> in the visual tree.</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject source)
    {
        DependencyObject? current = System.Windows.Media.VisualTreeHelper.GetParent(source);
        while (current is not null)
        {
            if (current is ScrollViewer viewer) return viewer;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    /// Quiet update check on startup; never disturbs normal work.
    /// </summary>
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Explorer/double-click launches are usually allowed to take the foreground; calling
        // Activate also covers a launch that started behind an already-open browser window.
        Activate();
        try
        {
            await UpdateCoordinator.RunCheckAsync(this, _viewModel.Settings, silent: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("启动更新检查意外失败", ex);
        }
    }

    /// <summary>
    /// Rounds the four window corners through DWM (Windows 11 native), matching the reference
    /// UI's curved corners. The custom chrome (GlassFrameThickness=0) leaves the frame square
    /// otherwise. On Windows 10 the attribute is rejected by DwmSetWindowAttribute — we ignore
    /// the result and keep square corners, everything else is unaffected.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>
    /// A scan or a batch still running means unsaved work; closing used to kill the scanner
    /// mid-document with no warning. Ask once, and only then let the window go.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.ConfirmExit())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Stops background services (the LAN receiver frees its port immediately) before the
    /// process exits.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Shutdown();
        base.OnClosed(e);
    }

    private void SyncListVisibility()
    {
        // Per-section visibility: a full scan history must not hide 去水印's empty state.
        bool hasJobs = _viewModel.Board.VisibleJobCount > 0;
        JobList.Visibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Visibility = hasJobs ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDragState(e);

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDragState(e);

    /// <summary>
    /// Every drag heartbeat: evaluate the payload against the selected feature and re-arm the
    /// exit watchdog. The visual state lives in the view model; this code never touches the tree.
    /// </summary>
    private void UpdateDragState(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            bool accepted = !_viewModel.IsBusy && files.Any(_viewModel.CanAcceptPath);
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            _viewModel.NotifyDragOver(files);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            _viewModel.NotifyDragOver(null);
        }

        e.Handled = true;
        _dragWatchdog.Stop();
        _dragWatchdog.Start();
    }

    /// <summary>A DragLeave bubbled up (child boundary crossed, or a real exit) — don't reset
    /// immediately, just start the clock and let the heartbeat / cursor check decide.</summary>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragWatchdog.Stop();
        _dragWatchdog.Start();
    }

    private void OnDragWatchdogTick(object? sender, EventArgs e)
    {
        _dragWatchdog.Stop();

        // Only reset when the cursor is genuinely outside the window: a bubbled child
        // DragLeave with the cursor still inside keeps the current state until the next
        // heartbeat re-syncs it.
        var p = System.Windows.Input.Mouse.GetPosition(this);
        bool inside = p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        if (inside) return;

        _viewModel.NotifyDragLeave();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dragWatchdog.Stop();
        _viewModel.NotifyDragLeave();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        // Everything goes to the view model: it expands folders, keeps what the feature handles
        // and reports the rejects (a mixed drop must not lose files without saying so).
        await _viewModel.AddFilesAsync(dropped);
    }

    private async void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _viewModel.FilePickerTitle,
            Filter = _viewModel.FilePickerFilter,
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true) return;
        await _viewModel.AddFilesAsync(dialog.FileNames);
    }

    /// <summary>First step of the account flow: clicking the user entry toggles the menu.</summary>
    private void OnUserEntryClick(object sender, RoutedEventArgs e) =>
        UserMenuPopup.IsOpen = !UserMenuPopup.IsOpen;

    /// <summary>Second step: the "设置" row enters the settings second-level page.</summary>
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        UserMenuPopup.IsOpen = false;
        _viewModel.OpenSettings();
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        UserMenuPopup.IsOpen = false;
        Close();
    }

    // Self-drawn caption buttons — with GlassFrameThickness=0 the system ones no longer exist.
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Collapses / expands the sidebar; a pure view-state toggle.</summary>
    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        bool isOpen = SidebarColumn.Width.Value > 0;
        SidebarColumn.Width = isOpen ? new GridLength(0) : new GridLength(208);
        SidebarBorder.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggleButton.ToolTip = isOpen ? "展开侧边栏" : "折叠侧边栏";
    }

    /// <summary>Flips the maximize/restore glyph when the window state changes.</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    /// <summary>
    /// Clicking anywhere outside the app (another window, the desktop) deactivates this window —
    /// no WPF mouse event reaches us, so Deactivated is the only reliable "clicked away" signal.
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        UserMenuPopup.IsOpen = false;
        CommitActiveRenames();
    }

    /// <summary>
    /// Clicking outside the rename box but inside the window commits it. WPF only moves keyboard
    /// focus to focusable elements, so TextBox.LostFocus alone never fires when the user clicks
    /// empty space.
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // A real mouse-down can't happen mid-drag (the OLE drag loop owns the mouse), so any
        // click also clears a drop zone left stale by a cancelled drag (e.g. pressing Esc).
        _viewModel.NotifyDragLeave();

        // Close the account menu on any press inside the main window — except the press on
        // the entry itself, whose Click handler stays a deterministic open/close toggle.
        if (UserMenuPopup.IsOpen &&
            !IsWithin(e.OriginalSource as DependencyObject, UserEntryButton))
            UserMenuPopup.IsOpen = false;

        if (!_viewModel.Board.Jobs.Any(j => j.IsEditing)) return;

        // A click inside the edit box (text area, selection, caret) must not commit.
        if (IsWithinRenameBox(e.OriginalSource as DependencyObject)) return;

        CommitActiveRenames();
    }

    /// <summary>Walks the visual tree up from <paramref name="source"/> looking for <paramref name="target"/>.</summary>
    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, target)) return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void CommitActiveRenames()
    {
        foreach (var job in _viewModel.Board.Jobs.Where(j => j.IsEditing).ToList())
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
