using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App.Services;

/// <summary>Tools the annotation toolbar can arm.</summary>
public enum ScreenshotTool
{
    None,
    Rectangle,
    Arrow,
    Pen,
    Text,
    Mosaic,
}

/// <summary>What a capture produced; <see cref="Canceled"/> when the user pressed Esc.</summary>
public sealed record ScreenshotCaptureResult(
    string? SavedPath, int Width, int Height, bool Copied, string? Error, bool Canceled);

/// <summary>
/// State shared by every monitor overlay plus the capture/annotate/save workflow. One session
/// owns the desktop bitmap, the selection (in global physical pixels), the annotations and the
/// per-monitor overlay windows — so a selection can be dragged across monitors while each
/// overlay still renders its own DPI-correct slice.
/// </summary>
public sealed class ScreenshotOverlaySession
{
    private enum Mode
    {
        None,
        Creating,
        Moving,
        Resizing,
        Drawing,
    }

    private readonly AppSettings _settings;
    private readonly DesktopCapture _desktop;
    private readonly TaskCompletionSource<ScreenshotCaptureResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ScreenshotOverlayWindow> _windows = new();
    private readonly List<ScreenshotAnnotation> _annotations = new();

    private ScreenshotRect? _selection;
    private Mode _mode;
    private Point _dragStart;
    private ScreenshotRect _dragOriginal;
    private ScreenshotHandle _dragHandle;
    private ScreenshotAnnotation? _draft;
    private ScreenshotTool _tool;
    private Point? _textAnchor;
    private MonitorInfo? _toolbarMonitor;
    private SKBitmap? _preview;
    private int _previewVersion;
    private bool _closed;

    private ScreenshotOverlaySession(AppSettings settings, DesktopCapture desktop)
    {
        _settings = settings;
        _desktop = desktop;
    }

    /// <summary>
    /// Hides the host window (so it is not in the screenshot), captures the desktop and shows
    /// one overlay per monitor. Always restores the host, whatever the outcome.
    /// </summary>
    public static async Task<ScreenshotCaptureResult?> RunAsync(Window? owner, AppSettings settings)
    {
        bool hideOwner = owner is { IsVisible: true, WindowState: not WindowState.Minimized };
        WindowState previousState = owner?.WindowState ?? WindowState.Normal;

        if (hideOwner) owner!.Hide();

        DesktopCapture? capture = null;
        ScreenshotOverlaySession? session = null;
        try
        {
            // Give DWM a beat to repaint where the app window just disappeared.
            await Task.Delay(200).ConfigureAwait(true);
            capture = await Task.Run(ScreenCaptureService.Capture).ConfigureAwait(true);
            session = new ScreenshotOverlaySession(settings, capture);
            session.ShowOverlays();

            AppLog.Info($"截图开始：{capture.Monitors.Count} 个显示器，虚拟屏幕 {capture.Bounds.Width}×{capture.Bounds.Height}");
            return await session._completion.Task.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("截图失败", ex);
            return new ScreenshotCaptureResult(null, 0, 0, false, $"截图失败：{ex.Message}", false);
        }
        finally
        {
            session?.Close();
            capture?.Dispose();
            RestoreOwner(owner, hideOwner, previousState);
        }
    }

    private static void RestoreOwner(Window? owner, bool wasHidden, WindowState state)
    {
        if (owner is null || !wasHidden) return;
        owner.Show();
        owner.WindowState = state;
        owner.Activate();
    }

    // --- Overlay lifecycle -----------------------------------------------------

    private void ShowOverlays()
    {
        foreach (MonitorInfo monitor in _desktop.Monitors)
        {
            var window = new ScreenshotOverlayWindow(this, monitor);
            _windows.Add(window);
        }

        foreach (ScreenshotOverlayWindow window in _windows)
            window.ShowForCapture();
    }

    private void Close()
    {
        _closed = true;
        foreach (ScreenshotOverlayWindow window in _windows)
        {
            try { window.Close(); } catch (Exception ex) { AppLog.Trace("关闭截图遮罩失败", ex); }
        }
        _windows.Clear();
        _preview?.Dispose();
        _preview = null;
    }

    public void RaiseChanged()
    {
        if (_closed) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    // --- Data the overlays render ---------------------------------------------

    public SKBitmap DesktopBitmap => _desktop.Bitmap;

    public ScreenshotRect DesktopBounds => _desktop.Bounds;

    public IReadOnlyList<MonitorInfo> Monitors => _desktop.Monitors;

    public ScreenshotRect? Selection => _selection;

    public bool HasSelection => _selection is { IsEmpty: false };

    public bool HasAnnotations => _annotations.Count > 0;

    public bool IsSelecting => _mode == Mode.Creating;

    public ScreenshotTool Tool => _tool;

    /// <summary>The in-progress annotation, in global physical pixels (overlay-only preview).</summary>
    public ScreenshotAnnotation? Draft => _draft;

    public Point? TextAnchor => _textAnchor;

    public MonitorInfo? ToolbarMonitor => _toolbarMonitor;

    public SKBitmap? PreviewBitmap => _preview;

    public int PreviewVersion => _previewVersion;

    /// <summary>The monitor containing a point, or the nearest one when the point is in a gap.</summary>
    public MonitorInfo MonitorAt(Point point)
    {
        foreach (MonitorInfo monitor in _desktop.Monitors)
        {
            if (point.X >= monitor.Left && point.X < monitor.Right &&
                point.Y >= monitor.Top && point.Y < monitor.Bottom)
            {
                return monitor;
            }
        }

        return _desktop.Monitors
            .OrderBy(m => DistanceTo(m, point))
            .First();
    }

    private static double DistanceTo(MonitorInfo monitor, Point point)
    {
        double dx = Math.Max(0, Math.Max(monitor.Left - point.X, point.X - monitor.Right));
        double dy = Math.Max(0, Math.Max(monitor.Top - point.Y, point.Y - monitor.Bottom));
        return dx * dx + dy * dy;
    }

    private double ScaleAt(Point point) => MonitorAt(point).Scale;

    // --- Pointer interaction ----------------------------------------------------

    public void Press(Point global)
    {
        if (_textAnchor is not null) return;

        if (_selection is not ScreenshotRect existing || existing.IsEmpty)
        {
            BeginSelection(global);
            return;
        }

        ScreenshotRect selection = existing;
        if (_tool != ScreenshotTool.None)
        {
            if (selection.Contains(global.X, global.Y))
            {
                if (_tool == ScreenshotTool.Text)
                {
                    _textAnchor = global;
                    RaiseChanged();
                    return;
                }

                _mode = Mode.Drawing;
                _dragStart = global;
                _draft = NewDraft(global, global);
                RaiseChanged();
                return;
            }

            BeginSelection(global);
            return;
        }

        ScreenshotHandle handle = ScreenshotGeometry.HitTest(selection, global.X, global.Y);
        if (handle == ScreenshotHandle.None)
        {
            BeginSelection(global);
            return;
        }

        // Once anything is drawn the selection is frozen: moving or resizing it would leave
        // the marks pointing at the wrong pixels.
        if (HasAnnotations) return;

        if (handle == ScreenshotHandle.Inside)
        {
            _mode = Mode.Moving;
            _dragStart = global;
            _dragOriginal = selection;
        }
        else
        {
            _mode = Mode.Resizing;
            _dragHandle = handle;
            _dragStart = global;
            _dragOriginal = selection;
        }
    }

    public void Move(Point global)
    {
        switch (_mode)
        {
            case Mode.Creating:
                _selection = ScreenshotRect
                    .FromPoints(_dragStart.X, _dragStart.Y, global.X, global.Y)
                    .ClampTo(_desktop.Bounds);
                RaiseChanged();
                break;

            case Mode.Moving:
            {
                double dx = global.X - _dragStart.X;
                double dy = global.Y - _dragStart.Y;
                double left = ClampAxis(_dragOriginal.Left + dx, _dragOriginal.Width, _desktop.Bounds.Left, _desktop.Bounds.Right);
                double top = ClampAxis(_dragOriginal.Top + dy, _dragOriginal.Height, _desktop.Bounds.Top, _desktop.Bounds.Bottom);
                _selection = new ScreenshotRect(left, top, _dragOriginal.Width, _dragOriginal.Height);
                RaiseChanged();
                break;
            }

            case Mode.Resizing:
                _selection = ScreenshotGeometry.Resize(_dragOriginal, _dragHandle, global.X, global.Y, _desktop.Bounds);
                _toolbarMonitor = MonitorAt(new Point(_selection.Value.CenterX, _selection.Value.CenterY));
                RaiseChanged();
                break;

            case Mode.Drawing:
                UpdateDraft(global);
                RaiseChanged();
                break;
        }
    }

    /// <summary>Keeps a moved rect's size while stopping its edges at the desktop boundary.</summary>
    private static double ClampAxis(double proposed, double size, double min, double max)
    {
        if (size >= max - min) return min;
        return Math.Clamp(proposed, min, max - size);
    }

    public void Release(Point global)
    {
        switch (_mode)
        {
            case Mode.Creating:
            {
                _mode = Mode.None;
                var selection = _selection?.Normalized().ClampTo(_desktop.Bounds);
                if (selection is not { } rect ||
                    rect.Width < ScreenshotGeometry.MinimumSelectionSide ||
                    rect.Height < ScreenshotGeometry.MinimumSelectionSide)
                {
                    _selection = null;
                    _toolbarMonitor = null;
                }
                else
                {
                    _selection = rect;
                    _toolbarMonitor = MonitorAt(new Point(rect.CenterX, rect.CenterY));
                }

                RaiseChanged();
                break;
            }

            case Mode.Moving:
            case Mode.Resizing:
            {
                _mode = Mode.None;
                if (_selection is { } rect)
                    _toolbarMonitor = MonitorAt(new Point(rect.CenterX, rect.CenterY));
                RaiseChanged();
                break;
            }

            case Mode.Drawing:
                _mode = Mode.None;
                CommitDraft();
                RaiseChanged();
                break;
        }
    }

    private void BeginSelection(Point global)
    {
        DiscardAnnotations();
        _tool = ScreenshotTool.None;
        _mode = Mode.Creating;
        _dragStart = global;
        _selection = new ScreenshotRect(global.X, global.Y, 0, 0);
        _toolbarMonitor = null;
        RaiseChanged();
    }

    private void DiscardAnnotations()
    {
        if (_annotations.Count == 0) return;
        _annotations.Clear();
        RebuildPreview();
    }

    // --- Tools -----------------------------------------------------------------

    public void SelectTool(ScreenshotTool tool)
    {
        if (!HasSelection) return;
        _tool = _tool == tool ? ScreenshotTool.None : tool;
        _textAnchor = null;
        RaiseChanged();
    }

    public void Undo()
    {
        if (_annotations.Count == 0) return;
        _annotations.RemoveAt(_annotations.Count - 1);
        RebuildPreview();
        RaiseChanged();
    }

    public void ClearAnnotations()
    {
        if (_annotations.Count == 0) return;
        _annotations.Clear();
        RebuildPreview();
        RaiseChanged();
    }

    public void CancelText()
    {
        if (_textAnchor is null) return;
        _textAnchor = null;
        RaiseChanged();
    }

    public void CommitText(string text)
    {
        Point? anchor = _textAnchor;
        _textAnchor = null;
        if (anchor is { } point && _selection is { } selection && selection.Contains(point.X, point.Y)
            && !string.IsNullOrWhiteSpace(text))
        {
            float scale = (float)ScaleAt(point);
            var local = new ScreenshotPointF((float)(point.X - selection.Left), (float)(point.Y - selection.Top));
            // 16 DIP matches the overlay's text box, converted to physical pixels.
            _annotations.Add(ScreenshotAnnotation.TextAt(
                local.X, local.Y, text.Trim(), Math.Max(12, 16f * scale)));
            RebuildPreview();
        }

        RaiseChanged();
    }

    // --- Draft annotation -------------------------------------------------------

    private ScreenshotAnnotation NewDraft(Point start, Point end)
    {
        float thickness = (float)Math.Max(2, 2.5 * ScaleAt(start));
        return _tool switch
        {
            ScreenshotTool.Rectangle => ScreenshotAnnotation.Rectangle(
                (float)start.X, (float)start.Y, (float)end.X, (float)end.Y, thickness),
            ScreenshotTool.Arrow => ScreenshotAnnotation.Arrow(
                (float)start.X, (float)start.Y, (float)end.X, (float)end.Y, thickness),
            ScreenshotTool.Pen => ScreenshotAnnotation.Pen(
                new List<ScreenshotPointF> { new((float)start.X, (float)start.Y) }, thickness),
            ScreenshotTool.Mosaic => ScreenshotAnnotation.Mosaic(
                (float)start.X, (float)start.Y, (float)end.X, (float)end.Y, thickness),
            _ => ScreenshotAnnotation.Rectangle(
                (float)start.X, (float)start.Y, (float)end.X, (float)end.Y, thickness),
        };
    }

    private void UpdateDraft(Point end)
    {
        if (_draft is null) return;

        if (_draft.Kind == ScreenshotAnnotationKind.Pen)
        {
            var points = _draft.Points as List<ScreenshotPointF> ?? _draft.Points.ToList();
            var last = points[^1];
            if (Math.Abs(last.X - end.X) < 1.5f && Math.Abs(last.Y - end.Y) < 1.5f) return;
            points.Add(new ScreenshotPointF((float)end.X, (float)end.Y));
            _draft = RecreateDraft(_draft, points);
            return;
        }

        float thickness = _draft.Thickness;
        _draft = _draft.Kind switch
        {
            ScreenshotAnnotationKind.Arrow => ScreenshotAnnotation.Arrow(
                _draft.Points[0].X, _draft.Points[0].Y, (float)end.X, (float)end.Y, thickness),
            ScreenshotAnnotationKind.Mosaic => ScreenshotAnnotation.Mosaic(
                _draft.Points[0].X, _draft.Points[0].Y, (float)end.X, (float)end.Y, thickness),
            _ => ScreenshotAnnotation.Rectangle(
                _draft.Points[0].X, _draft.Points[0].Y, (float)end.X, (float)end.Y, thickness),
        };
    }

    private static ScreenshotAnnotation RecreateDraft(ScreenshotAnnotation source, IReadOnlyList<ScreenshotPointF> points) =>
        new()
        {
            Kind = source.Kind,
            Points = points,
            ColorArgb = source.ColorArgb,
            Thickness = source.Thickness,
            Text = source.Text,
            FontSize = source.FontSize,
        };

    private void CommitDraft()
    {
        ScreenshotAnnotation? draft = _draft;
        _draft = null;
        if (draft is null || _selection is not { } selection) return;

        var local = new List<ScreenshotPointF>(draft.Points.Count);
        foreach (ScreenshotPointF point in draft.Points)
            local.Add(new ScreenshotPointF((float)(point.X - selection.Left), (float)(point.Y - selection.Top)));

        if (draft.Kind != ScreenshotAnnotationKind.Pen)
        {
            ScreenshotRect bounds = ScreenshotRect.FromPoints(
                local[0].X, local[0].Y, local[^1].X, local[^1].Y);
            if (bounds.Width < 4 || bounds.Height < 4) return;
        }
        else if (local.Count < 2)
        {
            return;
        }

        _annotations.Add(RecreateDraft(draft, local));
        RebuildPreview();
    }

    private void RebuildPreview()
    {
        _preview?.Dispose();
        _preview = null;
        _previewVersion++;

        if (_annotations.Count == 0 || !HasSelection) return;

        ScreenshotRect selection = _selection!.Value;
        var crop = new SKRectI(
            (int)Math.Round(selection.Left),
            (int)Math.Round(selection.Top),
            (int)Math.Round(selection.Right),
            (int)Math.Round(selection.Bottom));
        try
        {
            _preview = ScreenshotComposer.Compose(_desktop.Bitmap, crop, _annotations);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"截图预览合成失败（{ex.Message}）");
        }
    }

    // --- Finish -----------------------------------------------------------------

    /// <summary>Saves the PNG, puts it on the clipboard and completes the session.</summary>
    public void Confirm()
    {
        if (_completion.Task.IsCompleted) return;
        if (!HasSelection)
        {
            Cancel();
            return;
        }

        ScreenshotRect selection = _selection!.Value;
        int width = Math.Max(1, (int)Math.Round(selection.Width));
        int height = Math.Max(1, (int)Math.Round(selection.Height));

        SKBitmap? output = null;
        string? savedPath = null;
        string? saveError = null;
        string? copyError = null;
        bool copied = false;

        try
        {
            var crop = new SKRectI(
                (int)Math.Round(selection.Left), (int)Math.Round(selection.Top),
                (int)Math.Round(selection.Right), (int)Math.Round(selection.Bottom));
            output = ScreenshotComposer.Compose(_desktop.Bitmap, crop, _annotations);

            try
            {
                string directory = ScreenshotNaming.ResolveDirectory(_settings);
                Directory.CreateDirectory(directory);
                string fileName = ScreenshotNaming.EnsureUnique(
                    directory, ScreenshotNaming.BuildFileName(DateTime.Now));
                savedPath = Path.Combine(directory, fileName);

                using SKImage image = SKImage.FromBitmap(output);
                using SKData? data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null) throw new InvalidOperationException("PNG 编码失败");
                File.WriteAllBytes(savedPath, data.ToArray());
            }
            catch (Exception ex)
            {
                saveError = $"保存失败：{ex.Message}";
                savedPath = null;
                AppLog.Error("截图保存失败", ex);
            }

            try
            {
                copied = TryCopyToClipboard(SkiaBitmapBridge.ToBitmapSource(output));
            }
            catch (Exception ex)
            {
                copyError = $"复制到剪贴板失败：{ex.Message}";
                AppLog.Warn(copyError);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("截图处理失败", ex);
            _completion.TrySetResult(new ScreenshotCaptureResult(
                null, width, height, false, $"截图处理失败：{ex.Message}", false));
            return;
        }
        finally
        {
            output?.Dispose();
        }

        string? error = (saveError, copyError) switch
        {
            (null, null) => null,
            (not null, null) => saveError,
            (null, not null) => copyError,
            _ => $"{saveError}；{copyError}",
        };

        _completion.TrySetResult(new ScreenshotCaptureResult(
            savedPath, width, height, copied, error, false));
    }

    /// <summary>Esc: nothing is written and the host window comes back.</summary>
    public void Cancel()
    {
        _completion.TrySetResult(new ScreenshotCaptureResult(null, 0, 0, false, null, true));
    }

    private static bool TryCopyToClipboard(BitmapSource image)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    AppLog.Warn($"复制截图到剪贴板失败：{ex.Message}");
                    return false;
                }

                Thread.Sleep(40);
            }
        }

        return false;
    }
}
