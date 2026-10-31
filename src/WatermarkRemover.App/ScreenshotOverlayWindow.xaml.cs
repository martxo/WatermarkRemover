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
    private static readonly Brush MarkBrush = new SolidColorBrush(Color.FromRgb(229, 57, 53));

    private readonly ScreenshotOverlaySession _session;
    private readonly MonitorInfo _monitor;
    private bool _loaded;
    private bool _dragging;
    private bool _textEditing;

    private Rectangle? _draftRect;
    private Line? _draftLine;
    private Polygon? _draftHead;
    private Polyline? _draftPen;
    private Rectangle? _draftMosaic;

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

        MarkBrush.Freeze();
        CreateDraftShapes();
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
        RectTool.Click += (_, _) => _session.SelectTool(ScreenshotTool.Rectangle);
        ArrowTool.Click += (_, _) => _session.SelectTool(ScreenshotTool.Arrow);
        PenTool.Click += (_, _) => _session.SelectTool(ScreenshotTool.Pen);
        TextTool.Click += (_, _) => _session.SelectTool(ScreenshotTool.Text);
        MosaicTool.Click += (_, _) => _session.SelectTool(ScreenshotTool.Mosaic);
        UndoButton.Click += (_, _) => _session.Undo();
        ClearButton.Click += (_, _) => _session.ClearAnnotations();
        CancelButton.Click += (_, _) => _session.Cancel();
        ConfirmButton.Click += (_, _) => _session.Confirm();
    }

    private void CreateDraftShapes()
    {
        _draftRect = new Rectangle
        {
            Stroke = MarkBrush,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
        };
        _draftLine = new Line
        {
            Stroke = MarkBrush,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
        };
        _draftHead = new Polygon
        {
            Fill = MarkBrush,
            Visibility = Visibility.Collapsed,
        };
        _draftPen = new Polyline
        {
            Stroke = MarkBrush,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
        };
        _draftMosaic = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            Visibility = Visibility.Collapsed,
        };

        foreach (Shape shape in new Shape[] { _draftRect, _draftLine, _draftHead, _draftPen, _draftMosaic })
            ShapeLayer.Children.Add(shape);
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

    private void DrawDraft()
    {
        _draftRect!.Visibility = Visibility.Collapsed;
        _draftLine!.Visibility = Visibility.Collapsed;
        _draftHead!.Visibility = Visibility.Collapsed;
        _draftPen!.Visibility = Visibility.Collapsed;
        _draftMosaic!.Visibility = Visibility.Collapsed;

        ScreenshotAnnotation? draft = _session.Draft;
        if (draft is null || draft.Points.Count < 2) return;

        Point first = LocalFromGlobal(new Point(draft.Points[0].X, draft.Points[0].Y));
        Point last = LocalFromGlobal(new Point(draft.Points[^1].X, draft.Points[^1].Y));

        switch (draft.Kind)
        {
            case ScreenshotAnnotationKind.Rectangle:
                Place(_draftRect!, Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
                    Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y));
                _draftRect!.Visibility = Visibility.Visible;
                break;

            case ScreenshotAnnotationKind.Arrow:
                _draftLine!.X1 = first.X;
                _draftLine!.Y1 = first.Y;
                _draftLine!.X2 = last.X;
                _draftLine!.Y2 = last.Y;
                _draftLine!.Visibility = Visibility.Visible;
                DrawArrowHead(first, last);
                break;

            case ScreenshotAnnotationKind.Pen:
            {
                var points = new PointCollection();
                foreach (ScreenshotPointF point in draft.Points)
                    points.Add(LocalFromGlobal(new Point(point.X, point.Y)));
                _draftPen!.Points = points;
                _draftPen!.Visibility = Visibility.Visible;
                break;
            }

            case ScreenshotAnnotationKind.Mosaic:
                Place(_draftMosaic!, Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
                    Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y));
                _draftMosaic!.Visibility = Visibility.Visible;
                break;
        }
    }

    private void DrawArrowHead(Point start, Point end)
    {
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double head = 12;
        double spread = Math.PI / 7;
        var points = new PointCollection
        {
            end,
            new Point(end.X - head * Math.Cos(angle - spread), end.Y - head * Math.Sin(angle - spread)),
            new Point(end.X - head * Math.Cos(angle + spread), end.Y - head * Math.Sin(angle + spread)),
        };

        _draftHead!.Points = points;
        _draftHead!.Visibility = Visibility.Visible;
    }

    private void DrawToolbar(double width, double height, bool hasSelection, Rect localSelection)
    {
        MonitorInfo? host = _session.ToolbarMonitor;
        if (!hasSelection || host is null || host != _monitor)
        {
            Hide(ToolbarHost);
            return;
        }

        RectTool.IsChecked = _session.Tool == ScreenshotTool.Rectangle;
        ArrowTool.IsChecked = _session.Tool == ScreenshotTool.Arrow;
        PenTool.IsChecked = _session.Tool == ScreenshotTool.Pen;
        TextTool.IsChecked = _session.Tool == ScreenshotTool.Text;
        MosaicTool.IsChecked = _session.Tool == ScreenshotTool.Mosaic;
        UndoButton.IsEnabled = _session.HasAnnotations;
        ClearButton.IsEnabled = _session.HasAnnotations;

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
            if (_textEditing) HideEditor();
            return;
        }

        MonitorInfo host = _session.MonitorAt(anchor.Value);
        if (host != _monitor)
        {
            if (_textEditing) HideEditor();
            return;
        }

        if (_textEditing) return;

        _textEditing = true;
        Point local = LocalFromGlobal(anchor.Value);
        double width = RootCanvas.ActualWidth;
        double height = RootCanvas.ActualHeight;
        double x = Math.Clamp(local.X + 4, 4, Math.Max(4, width - TextEditor.Width - 4));
        double y = Math.Clamp(local.Y, 4, Math.Max(4, height - TextEditor.Height - 4));
        Canvas.SetLeft(TextEditor, x);
        Canvas.SetTop(TextEditor, y);
        TextEditor.Text = string.Empty;
        TextEditor.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Keyboard.Focus(TextEditor);
            TextEditor.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void HideEditor()
    {
        _textEditing = false;
        TextEditor.Visibility = Visibility.Collapsed;
        Keyboard.Focus(RootCanvas);
    }

    private void CommitTextFromEditor()
    {
        if (!_textEditing) return;
        string text = TextEditor.Text;
        HideEditor();
        _session.CommitText(text);
    }

    private void CancelTextFromEditor()
    {
        if (!_textEditing) return;
        HideEditor();
        _session.CancelText();
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        Key key = RealKey(e);
        if (key == Key.Enter)
        {
            CommitTextFromEditor();
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            CancelTextFromEditor();
            e.Handled = true;
        }
    }

    private void OnTextEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (_textEditing) CommitTextFromEditor();
    }

    // --- Mouse -----------------------------------------------------------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource as DependencyObject, ToolbarHost)) return;
        if (IsWithin(e.OriginalSource as DependencyObject, TextEditor)) return;

        if (_textEditing)
        {
            CommitTextFromEditor();
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
        if (!_textEditing) Activate();
    }

    /// <summary>Crosshair + magnifier while no selection exists yet.</summary>
    private void UpdateHover(Point local, Point global)
    {
        if (_textEditing || _session.HasSelection)
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
        if (TextEditor.IsKeyboardFocusWithin) return;

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

        // Tool shortcuts, the same muscle memory desktop screenshot tools use.
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        ScreenshotTool? shortcut = key switch
        {
            Key.R => ScreenshotTool.Rectangle,
            Key.A => ScreenshotTool.Arrow,
            Key.P => ScreenshotTool.Pen,
            Key.T => ScreenshotTool.Text,
            Key.M => ScreenshotTool.Mosaic,
            _ => null,
        };

        if (shortcut is { } tool)
        {
            _session.SelectTool(tool);
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
