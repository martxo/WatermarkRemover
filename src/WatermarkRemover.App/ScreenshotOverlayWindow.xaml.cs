using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SkiaSharp;
using WatermarkRemover.App.Controls;
using WatermarkRemover.App.Editing;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App;

/// <summary>
/// One monitor's slice of the screenshot overlay: shows the captured pixels for that monitor,
/// dims everything outside the shared selection and hosts the crosshair, magnifier, annotation
/// toolbar and text editor. All state lives in <see cref="ScreenshotOverlaySession"/>, so a
/// selection dragged across two monitors renders correctly on both windows at their own DPI.
/// </summary>
public partial class ScreenshotOverlayWindow : Window
{
    private const int SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly ScreenshotOverlaySession _session;
    private readonly MonitorInfo _monitor;
    private bool _loaded;
    private bool _dragging;

    private BitmapSource? _previewSource;
    private int _previewCachedVersion = -1;
    private Int32Rect _previewCachedRect;

    public ScreenshotOverlayWindow(ScreenshotOverlaySession session, MonitorInfo monitor)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

        InitializeComponent();
        Width = monitor.Width / monitor.Scale;
        Height = monitor.Height / monitor.Scale;

        WireToolbar();

        _session.Changed += OnSessionChanged;
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    /// <summary>Positions the window in physical pixels and shows it on its monitor.</summary>
    public void ShowForCapture()
    {
        var helper = new WindowInteropHelper(this);
        IntPtr handle = helper.EnsureHandle();
        SetWindowPosition(handle);
        Show();
    }

    private void SetWindowPosition(IntPtr handle)
    {
        SetWindowPos(handle, HwndTopmost, _monitor.Left, _monitor.Top,
            _monitor.Width, _monitor.Height, SwpNoActivate);
    }

    // --- Setup -----------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WPF may have applied the monitor's DPI during Show; re-pin the window to the
        // physical monitor rect so the captured pixels line up exactly with the desktop.
        SetWindowPosition(new WindowInteropHelper(this).Handle);

        using SKBitmap? subset = SkiaBitmapBridge.Crop(_session.DesktopBitmap, new SKRectI(
            _monitor.Left - (int)Math.Round(_session.DesktopBounds.Left),
            _monitor.Top - (int)Math.Round(_session.DesktopBounds.Top),
            _monitor.Right - (int)Math.Round(_session.DesktopBounds.Left),
            _monitor.Bottom - (int)Math.Round(_session.DesktopBounds.Top)));
        if (subset is not null)
            ScreenImage.Source = SkiaBitmapBridge.ToBitmapSource(subset);

        // The window is positioned by SetWindowPos above; showing it invisible until now
        // prevents the one-frame "flash at the primary monitor origin" WPF does on Show.
        Opacity = 1;
        _loaded = true;
        Redraw();
    }

    private void WireToolbar()
    {
        Toolbar.ToolClick += (_, tool) => _session.SelectTool(tool);
        Toolbar.ColorClick += (_, argb) => _session.SetColor(argb);
        Toolbar.WidthClick += (_, dip) => _session.SetLineWidth(dip);
        Toolbar.FontSizeClick += (_, size) => _session.SetTextFontSize(size);
        Toolbar.UndoClick += (_, _) => _session.Undo();
        Toolbar.RedoClick += (_, _) => _session.Redo();
        Toolbar.ClearClick += (_, _) => _session.ClearAnnotations();
        LongCaptureButton.Click += (_, _) => _session.StartLongCapture();
        CancelButton.Click += (_, _) => _session.Cancel();
        ConfirmButton.Click += (_, _) => _session.Confirm();
        Editor.Completed += OnTextEditorCompleted;
    }

    /// <summary>文字浮层结束：文本 = 提交；null = 取消。</summary>
    private void OnTextEditorCompleted(object? sender, string? text)
    {
        if (text is null) _session.CancelText();
        else _session.CommitText(text);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _session.Changed -= OnSessionChanged;
        if (_dragging)
        {
            _dragging = false;
            Mouse.Capture(null);
        }

        // Alt+F4 on an overlay closes only that window; the whole capture must end together
        // (a completed session ignores this no-op).
        _session.Cancel();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Redraw();

    // --- Coordinates -----------------------------------------------------------

    private Point GlobalFromLocal(Point local) => new(
        _monitor.Left + local.X * _monitor.Scale,
        _monitor.Top + local.Y * _monitor.Scale);

    private Point LocalFromGlobal(Point global) => new(
        (global.X - _monitor.Left) / _monitor.Scale,
        (global.Y - _monitor.Top) / _monitor.Scale);

    private Rect LocalRect(ScreenshotRect rect) => new(
        (rect.Left - _monitor.Left) / _monitor.Scale,
        (rect.Top - _monitor.Top) / _monitor.Scale,
        rect.Width / _monitor.Scale,
        rect.Height / _monitor.Scale);

    // --- Rendering -------------------------------------------------------------

    /// <summary>
    /// A render failure must never leave an invisible, always-on-top window swallowing input:
    /// the error is logged and the overlay stays usable (Esc still cancels).
    /// </summary>
    private void Redraw()
    {
        try
        {
            RedrawCore();
        }
        catch (Exception ex)
        {
            Logging.AppLog.Error("截图遮罩渲染失败", ex);
        }
    }

    private void RedrawCore()
    {
        if (!_loaded) return;

        double width = RootCanvas.ActualWidth;
        double height = RootCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        ScreenImage.Width = width;
        ScreenImage.Height = height;
        // The shape/handle/hover canvases have no intrinsic size; giving them the window size
        // keeps their children laid out relative to the monitor and lets the shape layer clip
        // the draft to the selection (annotations never paint over the dimmed area).
        ShapeLayer.Width = width;
        ShapeLayer.Height = height;
        HandleLayer.Width = width;
        HandleLayer.Height = height;
        HoverLayer.Width = width;
        HoverLayer.Height = height;

        ScreenshotRect? rawSelection = _session.Selection;
        bool hasSelection = rawSelection is ScreenshotRect raw && !raw.IsEmpty;
        ScreenshotRect selection = hasSelection ? rawSelection!.Value : new ScreenshotRect(0, 0, 0, 0);
        Rect localSelection = LocalRect(selection);

        DrawDim(width, height, hasSelection, localSelection);
        ShapeLayer.Clip = hasSelection ? new RectangleGeometry(localSelection) : null;
        DrawPreview(hasSelection, selection, localSelection);
        DrawSelection(hasSelection, localSelection);
        DrawSizeLabel(width, height, hasSelection, selection, localSelection);
        DrawWindowHint(hasSelection);
        DrawHint(width, hasSelection);
        DrawDraft();
        DrawToolbar(width, height, hasSelection, localSelection);
        ShowTextEditor();

        RootCanvas.Cursor = hasSelection && _session.Tool == ScreenshotTool.Text
            ? Cursors.IBeam
            : Cursors.Cross;
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static void Hide(FrameworkElement element) => element.Visibility = Visibility.Collapsed;

    private void DrawDim(double width, double height, bool hasSelection, Rect selection)
    {
        if (!hasSelection)
        {
            Place(DimTop, 0, 0, width, height);
            DimTop.Visibility = Visibility.Visible;
            Hide(DimBottom);
            Hide(DimLeft);
            Hide(DimRight);
            return;
        }

        double left = Math.Clamp(selection.Left, 0, width);
        double top = Math.Clamp(selection.Top, 0, height);
        double right = Math.Clamp(selection.Right, 0, width);
        double bottom = Math.Clamp(selection.Bottom, 0, height);

        Place(DimTop, 0, 0, width, Math.Max(0, top));
        Place(DimBottom, 0, bottom, width, Math.Max(0, height - bottom));
        Place(DimLeft, 0, top, Math.Max(0, left), Math.Max(0, bottom - top));
        Place(DimRight, right, top, Math.Max(0, width - right), Math.Max(0, bottom - top));

        foreach (Rectangle band in new[] { DimTop, DimBottom, DimLeft, DimRight })
            band.Visibility = Visibility.Visible;
    }

    private void DrawPreview(bool hasSelection, ScreenshotRect selection, Rect localSelection)
    {
        SKBitmap? preview = _session.PreviewBitmap;
        if (!hasSelection || preview is null)
        {
            Hide(PreviewImage);
            return;
        }

        var intersection = selection.Intersect(_monitor.Bounds);
        if (intersection.IsEmpty)
        {
            Hide(PreviewImage);
            return;
        }

        var sourceRect = new Int32Rect(
            (int)Math.Round(intersection.Left - selection.Left),
            (int)Math.Round(intersection.Top - selection.Top),
            Math.Max(1, (int)Math.Round(intersection.Width)),
            Math.Max(1, (int)Math.Round(intersection.Height)));

        if (_previewCachedVersion != _session.PreviewVersion || !SameRect(_previewCachedRect, sourceRect))
        {
            _previewSource = null;
            using SKBitmap? subset = SkiaBitmapBridge.Crop(preview, new SKRectI(
                sourceRect.X, sourceRect.Y, sourceRect.X + sourceRect.Width, sourceRect.Y + sourceRect.Height));
            if (subset is not null) _previewSource = SkiaBitmapBridge.ToBitmapSource(subset);
            _previewCachedVersion = _session.PreviewVersion;
            _previewCachedRect = sourceRect;
        }

        if (_previewSource is null)
        {
            Hide(PreviewImage);
            return;
        }

        PreviewImage.Source = _previewSource;
        Place(PreviewImage,
            (intersection.Left - _monitor.Left) / _monitor.Scale,
            (intersection.Top - _monitor.Top) / _monitor.Scale,
            intersection.Width / _monitor.Scale,
            intersection.Height / _monitor.Scale);
        PreviewImage.Visibility = Visibility.Visible;
    }

    private static bool SameRect(Int32Rect a, Int32Rect b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    private void DrawSelection(bool hasSelection, Rect localSelection)
    {
        if (!hasSelection)
        {
            Hide(SelectionBorder);
            HideHandles();
            return;
        }

        Place(SelectionBorder, localSelection.Left, localSelection.Top, localSelection.Width, localSelection.Height);
        SelectionBorder.Visibility = Visibility.Visible;

        bool showHandles = !_session.HasAnnotations;
        SetHandle(HandleTopLeft, showHandles, localSelection.Left, localSelection.Top);
        SetHandle(HandleTop, showHandles, localSelection.Left + localSelection.Width / 2, localSelection.Top);
        SetHandle(HandleTopRight, showHandles, localSelection.Right, localSelection.Top);
        SetHandle(HandleLeft, showHandles, localSelection.Left, localSelection.Top + localSelection.Height / 2);
        SetHandle(HandleRight, showHandles, localSelection.Right, localSelection.Top + localSelection.Height / 2);
        SetHandle(HandleBottomLeft, showHandles, localSelection.Left, localSelection.Bottom);
        SetHandle(HandleBottom, showHandles, localSelection.Left + localSelection.Width / 2, localSelection.Bottom);
        SetHandle(HandleBottomRight, showHandles, localSelection.Right, localSelection.Bottom);
    }

    private static void SetHandle(Rectangle handle, bool visible, double centerX, double centerY)
    {
        if (!visible)
        {
            Hide(handle);
            return;
        }

        Canvas.SetLeft(handle, centerX - handle.Width / 2);
        Canvas.SetTop(handle, centerY - handle.Height / 2);
        handle.Visibility = Visibility.Visible;
    }

    private void HideHandles()
    {
        foreach (Rectangle handle in new[]
                 {
                     HandleTopLeft, HandleTop, HandleTopRight, HandleLeft,
                     HandleRight, HandleBottomLeft, HandleBottom, HandleBottomRight,
                 })
        {
            Hide(handle);
        }
    }

    private void DrawSizeLabel(double width, double height, bool hasSelection,
        ScreenshotRect selection, Rect localSelection)
    {
        if (!hasSelection)
        {
            Hide(SizeLabel);
            return;
        }

        SizeText.Text = $"{Math.Round(selection.Width)} × {Math.Round(selection.Height)}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelWidth = SizeLabel.DesiredSize.Width;
        double labelHeight = SizeLabel.DesiredSize.Height;

        double x = Math.Clamp(localSelection.Left, 4, Math.Max(4, width - labelWidth - 4));
        double y = localSelection.Top - labelHeight - 6;
        if (y < 4) y = localSelection.Top + 6;
        y = Math.Clamp(y, 4, Math.Max(4, height - labelHeight - 4));
        Canvas.SetLeft(SizeLabel, x);
        Canvas.SetTop(SizeLabel, y);
    }

    /// <summary>Highlights the application window under the pointer across this monitor.</summary>
    private void DrawWindowHint(bool hasSelection)
    {
        if (hasSelection || _session.HoverWindow is not { } hover)
        {
            Hide(WindowHintBorder);
            return;
        }

        double left = Math.Max(hover.Left, _monitor.Left);
        double top = Math.Max(hover.Top, _monitor.Top);
        double right = Math.Min(hover.Right, _monitor.Right);
        double bottom = Math.Min(hover.Bottom, _monitor.Bottom);
        if (right <= left || bottom <= top)
        {
            Hide(WindowHintBorder);
            return;
        }

        WindowHintBorder.Visibility = Visibility.Visible;
        Place(WindowHintBorder,
            (left - _monitor.Left) / _monitor.Scale,
            (top - _monitor.Top) / _monitor.Scale,
            (right - left) / _monitor.Scale,
            (bottom - top) / _monitor.Scale);
    }

    private void DrawHint(double width, bool hasSelection)
    {
        if (hasSelection)
        {
            Hide(HintBar);
            return;
        }

        HintBar.Visibility = Visibility.Visible;
        HintBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintBar, Math.Max(4, (width - HintBar.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBar, 24);
    }

    private void DrawDraft()
    {
        ScreenshotAnnotation? draft = _session.Draft;
        if (draft is null)
        {
            ShapeLayer.Children.Clear();
            return;
        }

        // Live preview uses the toolbar's colour and width, exactly what the PNG will get.
        // Geometry and the arrow head live in DraftShapeRenderer, shared with the markup window.
        DraftShapeRenderer.Render(ShapeLayer, draft, LocalFromGlobal, new DraftShapeRenderer.Options(
            DraftShapeRenderer.MarkColor(_session.ColorArgb),
            _session.LineWidth,
            Color.FromArgb(90, 0, 0, 0),
            Color.FromArgb(220, 255, 255, 255),
            new DoubleCollection { 3, 3 }));
    }

    private void DrawToolbar(double width, double height, bool hasSelection, Rect localSelection)
    {
        MonitorInfo? host = _session.ToolbarMonitor;
        if (!hasSelection || host is null || host != _monitor)
        {
            Hide(ToolbarHost);
            return;
        }

        Toolbar.Sync(
            _session.Tool,
            _session.ColorArgb,
            _session.LineWidth,
            _session.TextFontSize,
            _session.HasAnnotations,
            _session.CanRedo,
            _session.HasAnnotations);
        LongCaptureButton.IsEnabled = hasSelection && !_session.HasAnnotations;

        ToolbarHost.Visibility = Visibility.Visible;
        ToolbarHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double toolbarWidth = ToolbarHost.DesiredSize.Width;
        double toolbarHeight = ToolbarHost.DesiredSize.Height;

        double x = Math.Clamp(localSelection.Left, 4, Math.Max(4, width - toolbarWidth - 4));
        double y = localSelection.Bottom + 10;
        if (y + toolbarHeight > height - 4) y = localSelection.Top - toolbarHeight - 10;
        y = Math.Clamp(y, 4, Math.Max(4, height - toolbarHeight - 4));
        Canvas.SetLeft(ToolbarHost, x);
        Canvas.SetTop(ToolbarHost, y);
    }

    // --- Text annotation -------------------------------------------------------

    private void ShowTextEditor()
    {
        Point? anchor = _session.TextAnchor;
        if (anchor is null)
        {
            if (Editor.IsActive) Editor.Hide();
            return;
        }

        MonitorInfo host = _session.MonitorAt(anchor.Value);
        if (host != _monitor)
        {
            if (Editor.IsActive) Editor.Hide();
            return;
        }

        if (Editor.IsActive) return;

        Point local = LocalFromGlobal(anchor.Value);
        double width = RootCanvas.ActualWidth;
        double height = RootCanvas.ActualHeight;
        double x = Math.Clamp(local.X + 4, 4, Math.Max(4, width - Editor.Width - 4));
        double y = Math.Clamp(local.Y, 4, Math.Max(4, height - Editor.Height - 4));
        Editor.Start(new Point(x, y), _session.TextFontSize,
            DraftShapeRenderer.MarkColor(_session.ColorArgb), Editor.Width, Editor.Height);
    }

    // --- Mouse -----------------------------------------------------------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource as DependencyObject, ToolbarHost)) return;
        if (IsWithin(e.OriginalSource as DependencyObject, Editor)) return;

        if (Editor.IsActive)
        {
            Editor.Submit();
            e.Handled = true;
            return;
        }

        Activate();
        Keyboard.Focus(RootCanvas);

        Point global = GlobalFromLocal(e.GetPosition(RootCanvas));
        _session.Press(global);
        _dragging = true;
        RootCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point local = e.GetPosition(RootCanvas);
        Point global = GlobalFromLocal(local);

        if (_dragging)
        {
            _session.Move(global);
            e.Handled = true;
            return;
        }

        UpdateHover(local, global);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        RootCanvas.ReleaseMouseCapture();
        Point global = GlobalFromLocal(e.GetPosition(RootCanvas));
        _session.Release(global);
        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!Editor.IsActive) Activate();
    }

    /// <summary>Crosshair + magnifier while no selection exists yet.</summary>
    private void UpdateHover(Point local, Point global)
    {
        if (Editor.IsActive || _session.HasSelection)
        {
            Hide(HoverLayer);
            return;
        }

        double width = RootCanvas.ActualWidth;
        double height = RootCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        HoverLayer.Visibility = Visibility.Visible;
        CrossVertical.X1 = local.X;
        CrossVertical.X2 = local.X;
        CrossVertical.Y1 = 0;
        CrossVertical.Y2 = height;
        CrossHorizontal.X1 = 0;
        CrossHorizontal.X2 = width;
        CrossHorizontal.Y1 = local.Y;
        CrossHorizontal.Y2 = local.Y;

        // 3x-ish pixel magnifier around the cursor, taken from the captured desktop.
        const int sample = 48;
        int originX = (int)Math.Round(_session.DesktopBounds.Left);
        int originY = (int)Math.Round(_session.DesktopBounds.Top);
        int x = Math.Clamp((int)Math.Round(global.X) - originX - sample / 2,
            0, Math.Max(0, _session.DesktopBitmap.Width - sample));
        int y = Math.Clamp((int)Math.Round(global.Y) - originY - sample / 2,
            0, Math.Max(0, _session.DesktopBitmap.Height - sample));
        using SKBitmap? subset = SkiaBitmapBridge.Crop(_session.DesktopBitmap, new SKRectI(x, y, x + sample, y + sample));
        if (subset is not null) MagnifierImage.Source = SkiaBitmapBridge.ToBitmapSource(subset);

        double magnifierX = local.X + 22;
        double magnifierY = local.Y + 22;
        if (magnifierX + Magnifier.Width > width) magnifierX = local.X - Magnifier.Width - 22;
        if (magnifierY + Magnifier.Height > height) magnifierY = local.Y - Magnifier.Height - 22;
        magnifierX = Math.Clamp(magnifierX, 4, Math.Max(4, width - Magnifier.Width - 4));
        magnifierY = Math.Clamp(magnifierY, 4, Math.Max(4, height - Magnifier.Height - 4));
        Canvas.SetLeft(Magnifier, magnifierX);
        Canvas.SetTop(Magnifier, magnifierY);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Editor.IsKeyboardFocusWithin) return;

        Key key = RealKey(e);
        if (key == Key.Escape)
        {
            _session.Cancel();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter)
        {
            _session.Confirm();
            e.Handled = true;
            return;
        }

        if (key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _session.Confirm();
            e.Handled = true;
            return;
        }

        if (key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _session.Undo();
            e.Handled = true;
            return;
        }

        if (key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _session.Redo();
            e.Handled = true;
            return;
        }

        // Tool shortcuts, the same muscle memory desktop screenshot tools use. The mapping
        // lives on the tools themselves — a newly registered tool brings its own key.
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        if (AnnotationToolRegistry.Default.FindByKey(key) is { } tool)
        {
            _session.SelectTool(tool.Tool);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The real key behind WPF's wrappers: Alt combinations arrive as Key.System and keys
    /// routed through an input method (or synthesized input) arrive as Key.ImeProcessed —
    /// without unwrapping, Enter and the single-letter tool shortcuts would silently do nothing.
    /// </summary>
    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, target)) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, int flags);
}
