using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Screenshot;

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

    /// <summary>System-wide shortcuts for the three screenshot actions; one service each so
    /// each gesture keeps its own registration id and fallback.</summary>
    private GlobalHotkeyService? _hotkeys;
    private GlobalHotkeyService? _fullScreenHotkeys;
    private GlobalHotkeyService? _shelfHotkeys;

    /// <summary>The gestures currently registered (null when none); restored if a new one fails.</summary>
    private ScreenshotHotkey? _activeHotkey;
    private ScreenshotHotkey? _activeFullScreenHotkey;
    private ScreenshotHotkey? _activeShelfHotkey;

    private ScreenshotShelfWindow? _shelf;
    private TrayIconService? _tray;
    private bool _forceExit;
    private WindowState _hiddenWindowState = WindowState.Normal;

    // Thumbnail gestures of a screenshot row: click copies, dragging hands the file to the target.
    private FileJobViewModel? _thumbnailDragJob;
    private Point _thumbnailDragStart;
    private bool _thumbnailDragStarted;

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
        // The screenshot overlay is a set of full-screen windows, so the host supplies it; the
        // view model only turns the result into a record and user-visible status.
        _viewModel.ScreenshotSaved += (_, job) => EnsureShelf().AddScreenshot(job);
        _viewModel.ScreenshotCaptureHandler = async () =>
        {
            // The line is topmost: hide it so it never appears inside the captured image.
            // 会话全程静默热区（取景时鼠标会划过屏幕边缘），结束再恢复。
            _shelf?.SuppressForCapture();
            try
            {
                return await ScreenshotOverlaySession.RunAsync(this, _viewModel.Settings).ConfigureAwait(true);
            }
            finally
            {
                _shelf?.ResumeAfterCapture();
            }
        };
        _viewModel.ScreenshotFullScreenHandler = async delaySeconds =>
        {
            _shelf?.SuppressForCapture();
            try
            {
                return await ScreenshotOverlaySession.CaptureFullScreenAsync(this, _viewModel.Settings, delaySeconds).ConfigureAwait(true);
            }
            finally
            {
                _shelf?.ResumeAfterCapture();
            }
        };
        // The settings page asks before saving: an occupied gesture is rejected there, so the
        // old shortcut and the saved settings both stay untouched. A gesture already used by
        // one of our own three features counts as free — saving re-registers all of them.
        _viewModel.ScreenshotHotkeyValidator = hotkey =>
        {
            if (_activeHotkey == hotkey || _activeFullScreenHotkey == hotkey || _activeShelfHotkey == hotkey)
                return null;
            return _hotkeys is null ? null
                : _hotkeys.CanRegister(hotkey, out string? error) ? null : error;
        };
        _viewModel.ScreenshotHotkeyChanged += (_, _) =>
        {
            RegisterScreenshotHotkeys(notifyFailure: true);
            // 设置保存后热更新触发区域：晾衣绳是长驻窗口，改配置不用重启。
            _shelf?.UpdateTrigger(_viewModel.Settings.ScreenshotTrigger);
        };
        _viewModel.ScreenshotShelfRequested += (_, _) => ToggleScreenshotShelf();
        _viewModel.ScreenshotPinRequested += (_, path) => PinnedImageWindow.ShowFor(path);
        _viewModel.ScreenshotOcrRequested += (_, path) => _ = ShowScreenshotOcrAsync(path);
        _viewModel.ScreenshotViewRequested += (_, path) =>
            ImageViewerWindow.ShowFor(path, _viewModel.Settings);
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
        // The line exists from startup: hovering the top edge must work even before the first
        // screenshot; it stays above the screen until the pointer asks for it.
        EnsureShelf();
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

        // The message loop exists now, so WM_HOTKEY has somewhere to land. Three ids: region
        // capture, full-screen capture and the shelf, each independently configurable.
        _hotkeys = new GlobalHotkeyService(hwnd, 0x5348, OnScreenshotHotkey);
        _fullScreenHotkeys = new GlobalHotkeyService(hwnd, 0x534B, OnFullScreenHotkey);
        _shelfHotkeys = new GlobalHotkeyService(hwnd, 0x534A, ToggleScreenshotShelf);
        RegisterScreenshotHotkeys();

        _tray = new TrayIconService(hwnd, ShowFromTray, () => _ = _viewModel.CaptureScreenshotAsync(),
            ToggleScreenshotShelf, ExitFromTray);
    }

    /// <summary>Restores the main window after it was hidden to the tray.</summary>
    private void ShowFromTray()
    {
        Show();
        WindowState = _hiddenWindowState == WindowState.Minimized
            ? WindowState.Normal
            : _hiddenWindowState;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Tray menu exit: the only path that actually ends the process.</summary>
    private void ExitFromTray()
    {
        _forceExit = true;
        Close();
    }

    /// <summary>Windows shutdown/logoff must be allowed through, not turned into a tray hide.</summary>
    internal void AllowExitOnSystemShutdown() => _forceExit = true;

    /// <summary>Runs the app-wide OCR engine on one screenshot and shows a copyable result.</summary>
    private async Task ShowScreenshotOcrAsync(string path)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            var engine = AppComposition.Shared.OcrEngine;
            IReadOnlyList<string> lines = await engine.RecognizeAsync(bytes).ConfigureAwait(true);
            string text = string.Join(Environment.NewLine, lines);
            new ScreenshotOcrWindow(text, engine.DisplayName).Show();
            AppLog.Info($"截图 OCR 完成：{path}，识别 {lines.Count} 行");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"截图 OCR 失败：{path}（{ex.Message}）");
            _dialogs.Warn($"文字识别失败：{ex.Message}", "截图文字识别");
        }
    }

    /// <summary>Long-press on the clothesline: edit a copy of the saved screenshot.</summary>
    private void ShowImageMarkup(FileJobViewModel job)
    {
        if (job.OutputPath is not { } path || !File.Exists(path)) return;

        try
        {
            var window = new ImageMarkupWindow(path, (target, width, height) =>
                _viewModel.AddAnnotatedScreenshot(target, width, height), _viewModel.Settings);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开标注窗口失败：{path}（{ex.Message}）");
            _dialogs.Warn($"无法打开标注窗口：{ex.Message}", "标注截图");
        }
    }

    /// <summary>Shows or hides the clothesline (hotkey, page button and tray share it).</summary>
    private void ToggleScreenshotShelf() => EnsureShelf().Toggle();

    /// <summary>
    /// Creates the clothesline once and keeps it alive above the screen; closing/creating it on
    /// every toggle would lose the swing animation and the pointer-edge reveal.
    /// </summary>
    private ScreenshotShelfWindow EnsureShelf()
    {
        if (_shelf is { } existing) return existing;

        var shelf = new ScreenshotShelfWindow(
            _viewModel.ScreenshotShelfItems, _viewModel.OpenScreenshotFolder,
            _viewModel.Settings.ScreenshotTrigger);
        shelf.MarkupRequested += (_, job) => ShowImageMarkup(job);
        shelf.Closed += (_, _) => _shelf = null;
        _shelf = shelf;
        // ShowActivated=False in XAML: the line appears without stealing focus from the app
        // the user is working in.
        shelf.Show();
        return shelf;
    }

    /// <summary>
    /// Re-points all three screenshot shortcuts. Every service releases its gesture first, so
    /// the three are cleared up front: swapping two gestures between slots would otherwise fail
    /// halfway through, while the target id still held the combination.
    /// A failed change is reported and the previous gesture is restored — silently saving a
    /// shortcut that does nothing is exactly the confusing case this avoids.
    /// </summary>
    private void RegisterScreenshotHotkeys(bool notifyFailure = false)
    {
        _hotkeys?.Clear();
        _fullScreenHotkeys?.Clear();
        _shelfHotkeys?.Clear();

        ScreenshotHotkey region = ScreenshotHotkey.ParseOrDefault(_viewModel.Settings.ScreenshotHotkey);
        ScreenshotHotkey fullScreen = ScreenshotHotkey.ParseOrDefault(
            _viewModel.Settings.ScreenshotFullScreenHotkey);
        ScreenshotHotkey shelf = ScreenshotHotkey.ParseOrDefault(_viewModel.Settings.ScreenshotShelfHotkey);

        bool regionOk = ApplyGesture(_hotkeys, region, ref _activeHotkey, "截图快捷键", notifyFailure, ScreenshotHotkey.Default);
        ApplyGesture(_fullScreenHotkeys, fullScreen, ref _activeFullScreenHotkey, "全屏截图快捷键", notifyFailure, ScreenshotHotkey.FullScreenDefault);
        ApplyGesture(_shelfHotkeys, shelf, ref _activeShelfHotkey, "截图晾衣绳快捷键", notifyFailure, ScreenshotHotkey.ShelfDefault);

        // The screenshot page's status line talks about the region gesture.
        _viewModel.ReportScreenshotHotkey(
            region, regionOk,
            regionOk ? null : "注册失败，可能已被其他程序占用",
            regionOk ? null : _activeHotkey);
    }

    /// <summary>Registers one gesture and puts the previous one back when it is rejected.</summary>
    private bool ApplyGesture(
        GlobalHotkeyService? service,
        ScreenshotHotkey requested,
        ref ScreenshotHotkey? active,
        string label,
        bool notifyFailure,
        ScreenshotHotkey builtInDefault)
    {
        if (service is null) return false;
        if (active == requested) return true;

        if (service.Register(requested, out string? error))
        {
            active = requested;
            return true;
        }

        // 回退顺序：上一个可用的 →（启动时没有上一个）内置默认键。任何一步成功都不能让
        // 功能处于「没有热键」的状态——那等于静默失效。
        bool hasPrevious = active is { } previous;
        ScreenshotHotkey? fallback = active ?? builtInDefault;
        bool restored = fallback is { } candidate && service.Register(candidate, out _);
        active = restored ? fallback : null;

        if (notifyFailure)
        {
            string kept = restored
                ? hasPrevious
                    ? $"\n\n仍继续使用原快捷键 {fallback}。"
                    : $"\n\n已临时回退到内置默认快捷键 {fallback}（你的设置保持不变，下次启动仍会尝试 {requested}）。"
                : string.Empty;
            _dialogs.Warn(
                $"{label} {requested} 未能注册：{error ?? "未知原因"}。{kept}\n\n" +
                "请换一个组合键，或先关闭占用该快捷键的程序；截图页面上的按钮始终可用。",
                label);
        }

        return false;
    }

    /// <summary>
    /// WM_HOTKEY: the view model guards against overlapping captures, so a double press (or a
    /// press while the page button already started one) simply does nothing. The settings page
    /// is ignored on purpose: recording a gesture there must not launch a capture over it.
    /// </summary>
    private void OnScreenshotHotkey()
    {
        if (_viewModel.IsSettingsOpen) return;
        _ = _viewModel.CaptureScreenshotAsync();
    }

    /// <summary>WM_HOTKEY for a full-screen capture (no region overlay).</summary>
    private void OnFullScreenHotkey()
    {
        if (_viewModel.IsSettingsOpen) return;
        _ = _viewModel.CaptureFullScreenAsync(0);
    }

    /// <summary>
    /// A scan or a batch still running means unsaved work; closing used to kill the scanner
    /// mid-document with no warning. Ask once, and only then let the window go.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Normal close hides to the tray so the global shortcuts and the shelf keep working.
        // The tray's 退出 (and a Windows shutdown) take the real exit path below.
        // Only hide when a real tray icon exists: hiding without one would leave the app
        // running invisibly with no way to bring it back.
        if (!_forceExit && !Environment.HasShutdownStarted &&
            _viewModel.Settings.MinimizeToTrayOnClose && _tray is { IsAdded: true })
        {
            _hiddenWindowState = WindowState;
            Hide();
            _tray?.ShowBackgroundHint();
            AppLog.Info("主窗口关闭请求已转为托盘后台运行");
            e.Cancel = true;
            return;
        }

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
        _hotkeys?.Dispose();
        _hotkeys = null;
        _shelfHotkeys?.Dispose();
        _shelfHotkeys = null;
        _activeHotkey = null;
        _shelf?.Close();
        _shelf = null;
        _tray?.Dispose();
        _tray = null;
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

    // --- Screenshot row gestures (Tendedero-style shelf) -----------------------

    private void OnScreenshotThumbnailMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Image { DataContext: FileJobViewModel job } || !job.CanDragScreenshot) return;

        _thumbnailDragJob = job;
        _thumbnailDragStart = e.GetPosition(JobList);
        _thumbnailDragStarted = false;
        // Keep the press away from the inline-rename hit area underneath.
        if (sender is UIElement element) element.CaptureMouse();
        e.Handled = true;
    }

    private void OnScreenshotThumbnailMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_thumbnailDragJob is null) return;

        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _thumbnailDragJob = null;
            if (sender is UIElement released) released.ReleaseMouseCapture();
            return;
        }

        Point position = e.GetPosition(JobList);
        bool moved = Math.Abs(position.X - _thumbnailDragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                     Math.Abs(position.Y - _thumbnailDragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!moved) return;

        FileJobViewModel job = _thumbnailDragJob;
        string? path = job.OutputPath;
        _thumbnailDragJob = null;
        _thumbnailDragStarted = true;
        if (sender is UIElement element) element.ReleaseMouseCapture();

        if (path is null || !File.Exists(path))
        {
            _thumbnailDragStarted = false;
            return;
        }

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Logging.AppLog.Warn($"拖出截图失败：{ex.Message}");
        }
        finally
        {
            _thumbnailDragStarted = false;
        }

        e.Handled = true;
    }

    private void OnScreenshotThumbnailMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is UIElement element) element.ReleaseMouseCapture();

        FileJobViewModel? job = _thumbnailDragJob;
        _thumbnailDragJob = null;
        bool dragged = _thumbnailDragStarted;
        _thumbnailDragStarted = false;
        if (job is null || dragged) return;

        // Same gesture as a PDF row's thumbnail: a click opens instead of copying, so every
        // record in the list behaves the same way. Copy stays available in the row's actions.
        job.OpenImageCommand.Execute(null);
        e.Handled = true;
    }
}
