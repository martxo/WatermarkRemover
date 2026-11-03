using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WatermarkRemover.App.Editing;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Imaging.Encoding;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App.Services;

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
    private readonly AnnotationEditSession _edit = new()
    {
        // 画布坐标 = 全局物理像素；粗细/字号按起笔点（或文字锚点）所在显示器的 scale 换算。
        StrokeFloor = 2f,
        MinMarkSpan = 4f,
        MinPenStep = 1.5f,
        RequireBothSides = true,
    };

    private ScreenshotRect? _selection;
    private Mode _mode;
    private Point _dragStart;
    private ScreenshotRect _dragOriginal;
    private ScreenshotHandle _dragHandle;
    private ScreenshotRect? _hoverWindow;
    private Point _hoverProbePoint = new(double.NaN, double.NaN);
    private MonitorInfo? _toolbarMonitor;
    private SKBitmap? _preview;
    private int _previewVersion;
    private LongScreenshotWindow? _longWindow;
    private bool _closed;

    private ScreenshotOverlaySession(AppSettings settings, DesktopCapture desktop)
    {
        _settings = settings;
        _desktop = desktop;

        _edit.ScaleAt = point => (float)ScaleAt(point);
        _edit.ToLocal = ToLocal;
        _edit.CanPlaceText = point => _selection is { } selection && selection.Contains(point.X, point.Y);
        // 内核的变化翻译回截图侧的两条既有通道：重算合成预览 + 通知各显示器重绘。
        _edit.HistoryChanged += (_, _) => RebuildPreview();
        _edit.Changed += (_, _) => RaiseChanged();
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

    /// <summary>
    /// Captures the whole desktop without the selection overlay (PixPin-style full-screen
    /// capture). A positive <paramref name="delaySeconds"/> gives the user time to arrange
    /// windows before the app hides and the shutter fires.
    /// </summary>
    public static async Task<ScreenshotCaptureResult?> CaptureFullScreenAsync(
        Window? owner, AppSettings settings, int delaySeconds)
    {
        bool hideOwner = owner is { IsVisible: true, WindowState: not WindowState.Minimized };
        WindowState previousState = owner?.WindowState ?? WindowState.Normal;

        DesktopCapture? capture = null;
        try
        {
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(true);
            if (hideOwner) owner!.Hide();
            await Task.Delay(200).ConfigureAwait(true);

            capture = await Task.Run(ScreenCaptureService.Capture).ConfigureAwait(true);
            using SKBitmap output = ScreenshotComposer.Compose(
                capture.Bitmap,
                new SKRectI(0, 0, capture.Bitmap.Width, capture.Bitmap.Height),
                Array.Empty<ScreenshotAnnotation>());

            ScreenshotCaptureResult result = SaveAndCopy(output, settings, output.Width, output.Height);
            AppLog.Info($"全屏截图完成：{result.Width}×{result.Height}，复制{(result.Copied ? "成功" : "失败")}");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("全屏截图失败", ex);
            return new ScreenshotCaptureResult(null, 0, 0, false, $"全屏截图失败：{ex.Message}", false);
        }
        finally
        {
            capture?.Dispose();
            if (hideOwner)
            {
                owner!.Show();
                owner.WindowState = previousState;
                owner.Activate();
            }
        }
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

    public bool HasAnnotations => _edit.HasAnnotations;

    public bool IsSelecting => _mode == Mode.Creating;

    public ScreenshotTool Tool => _edit.Tool;

    /// <summary>Mark colour chosen in the toolbar (ARGB).</summary>
    public uint ColorArgb => _edit.Style.ColorArgb;

    /// <summary>Stroke width in device-independent pixels; converted to physical per monitor.</summary>
    public float LineWidth => _edit.Style.LineWidthDip;

    /// <summary>Text annotation font size in device-independent pixels (12 / 16 / 20).</summary>
    public float TextFontSize => _edit.Style.TextFontSizeDip;

    /// <summary>Whether an undone annotation can be restored (Ctrl+Y).</summary>
    public bool CanRedo => _edit.CanRedo;

    /// <summary>Window under the pointer before a selection exists (PixPin-style hover highlight).</summary>
    public ScreenshotRect? HoverWindow => _hoverWindow;

    /// <summary>The in-progress annotation, in global physical pixels (overlay-only preview).</summary>
    public ScreenshotAnnotation? Draft => _edit.Draft;

    public Point? TextAnchor => _edit.TextAnchor;

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
        if (_edit.TextAnchor is not null) return;

        if (_selection is not ScreenshotRect existing || existing.IsEmpty)
        {
            // A click without a drag selects the whole window under the pointer; probing here
            // also makes a click work when the mouse has not moved since the overlay opened.
            UpdateHover(global);
            BeginSelection(global);
            return;
        }

        ScreenshotRect selection = existing;
        ScreenshotTool tool = _edit.Tool;
        if (tool != ScreenshotTool.None)
        {
            if (selection.Contains(global.X, global.Y))
            {
                if (tool == ScreenshotTool.Text)
                {
                    _edit.Begin(global);
                    return;
                }

                _mode = Mode.Drawing;
                _dragStart = global;
                _edit.Begin(global);
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
        if (_mode == Mode.None && (_selection is not { } current || current.IsEmpty))
            UpdateHover(global);

        switch (_mode)
        {
            case Mode.Creating:
                _selection = SnapToScreenEdges(ScreenshotRect
                    .FromPoints(_dragStart.X, _dragStart.Y, global.X, global.Y))
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
                _selection = SnapToScreenEdges(
                        ScreenshotGeometry.Resize(_dragOriginal, _dragHandle, global.X, global.Y, _desktop.Bounds))
                    .ClampTo(_desktop.Bounds);
                _toolbarMonitor = MonitorAt(new Point(_selection.Value.CenterX, _selection.Value.CenterY));
                RaiseChanged();
                break;

            case Mode.Drawing:
                _edit.Update(global);
                RaiseChanged();
                break;
        }
    }

    /// <summary>Snaps selection edges to desktop/monitor boundaries within a few pixels.</summary>
    private ScreenshotRect SnapToScreenEdges(ScreenshotRect rect)
    {
        var vertical = new List<double> { _desktop.Bounds.Left, _desktop.Bounds.Right };
        var horizontal = new List<double> { _desktop.Bounds.Top, _desktop.Bounds.Bottom };
        foreach (MonitorInfo monitor in _desktop.Monitors)
        {
            vertical.Add(monitor.Left);
            vertical.Add(monitor.Right);
            horizontal.Add(monitor.Top);
            horizontal.Add(monitor.Bottom);
        }

        return ScreenshotGeometry.SnapEdges(rect, vertical, horizontal);
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
                bool tooSmall = selection is not { } candidate ||
                    candidate.Width < ScreenshotGeometry.MinimumSelectionSide ||
                    candidate.Height < ScreenshotGeometry.MinimumSelectionSide;
                if (tooSmall)
                {
                    // A simple click (not a drag) adopts the window under the pointer.
                    if (_hoverWindow is { } hover &&
                        hover.Width >= ScreenshotGeometry.MinimumSelectionSide &&
                        hover.Height >= ScreenshotGeometry.MinimumSelectionSide)
                    {
                        _selection = hover.ClampTo(_desktop.Bounds);
                        _toolbarMonitor = MonitorAt(new Point(_selection.Value.CenterX, _selection.Value.CenterY));
                    }
                    else
                    {
                        _selection = null;
                        _toolbarMonitor = null;
                    }
                }
                else
                {
                    _selection = selection;
                    _toolbarMonitor = MonitorAt(new Point(selection!.Value.CenterX, selection.Value.CenterY));
                }

                if (_selection is not null) ClearHover();

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
                _edit.Commit();
                RaiseChanged();
                break;
        }
    }

    /// <summary>Throttled window probe so hovering stays cheap even at 120 Hz pointer moves.</summary>
    private void UpdateHover(Point global)
    {
        if (!double.IsNaN(_hoverProbePoint.X) &&
            Math.Abs(global.X - _hoverProbePoint.X) < 3 &&
            Math.Abs(global.Y - _hoverProbePoint.Y) < 3)
        {
            return;
        }

        _hoverProbePoint = global;
        ScreenshotRect? hover = ScreenshotWindowDetector.FindAt(global.X, global.Y);
        if (hover is { } candidate) hover = candidate.ClampTo(_desktop.Bounds);
        if (hover == _hoverWindow) return;

        _hoverWindow = hover;
        RaiseChanged();
    }

    private void ClearHover()
    {
        if (_hoverWindow is null) return;
        _hoverWindow = null;
        _hoverProbePoint = new Point(double.NaN, double.NaN);
        RaiseChanged();
    }

    private void BeginSelection(Point global)
    {
        _edit.Clear();
        _edit.SelectTool(ScreenshotTool.None);
        _mode = Mode.Creating;
        _dragStart = global;
        _selection = new ScreenshotRect(global.X, global.Y, 0, 0);
        _toolbarMonitor = null;
        RaiseChanged();
    }

    // --- Tools -----------------------------------------------------------------

    public void SelectTool(ScreenshotTool tool)
    {
        if (!HasSelection) return;
        _edit.SelectTool(tool);
        RaiseChanged();
    }

    public void SetColor(uint colorArgb) => _edit.SetColor(colorArgb);

    public void SetLineWidth(float lineWidthDip) => _edit.SetLineWidth(lineWidthDip);

    public void SetTextFontSize(float fontSizeDip) => _edit.SetTextFontSize(fontSizeDip);

    /// <summary>
    /// Leaves normal capture mode and opens the manual long-screenshot stitcher. Annotations are
    /// not carried into long mode: each appended frame is raw desktop content.
    /// </summary>
    public void StartLongCapture()
    {
        if (_completion.Task.IsCompleted || _longWindow is not null) return;
        if (_selection is not { } selection || selection.IsEmpty || HasAnnotations) return;

        SKBitmap first;
        try
        {
            first = ScreenshotComposer.Compose(
                _desktop.Bitmap,
                new SKRectI(
                    (int)Math.Round(selection.Left), (int)Math.Round(selection.Top),
                    (int)Math.Round(selection.Right), (int)Math.Round(selection.Bottom)),
                Array.Empty<ScreenshotAnnotation>());
        }
        catch (Exception ex)
        {
            AppLog.Error("长截图首屏处理失败", ex);
            return;
        }

        foreach (ScreenshotOverlayWindow window in _windows)
            window.Hide();

        try
        {
            _longWindow = new LongScreenshotWindow(first, selection, _settings, FinishLongCapture, CancelLongCapture);
        }
        catch (Exception ex)
        {
            AppLog.Error("打开长截图面板失败", ex);
            first.Dispose();
            foreach (ScreenshotOverlayWindow window in _windows)
                window.ShowForCapture();
            return;
        }

        _longWindow.Closed += (_, _) => _longWindow = null;
        _longWindow.Show();
        _longWindow.Activate();
    }

    private void FinishLongCapture(ScreenshotCaptureResult result)
    {
        _completion.TrySetResult(result);
        Close();
    }

    private void CancelLongCapture() => FinishLongCapture(
        new ScreenshotCaptureResult(null, 0, 0, false, null, true));

    public void Undo() => _edit.Undo();

    public void Redo() => _edit.Redo();

    public void ClearAnnotations() => _edit.Clear();

    public void CancelText() => _edit.CancelText();

    public void CommitText(string text) => _edit.CommitText(text);

    // --- Draft annotation -------------------------------------------------------

    /// <summary>画布全局坐标 → 选区内标注坐标（与旧 <c>CommitDraft</c> 的换算一致）。</summary>
    private Point ToLocal(Point point) =>
        _selection is { } selection
            ? new Point(point.X - selection.Left, point.Y - selection.Top)
            : point;

    private void RebuildPreview()
    {
        _preview?.Dispose();
        _preview = null;
        _previewVersion++;

        if (_edit.Annotations.Count == 0 || !HasSelection) return;

        ScreenshotRect selection = _selection!.Value;
        var crop = new SKRectI(
            (int)Math.Round(selection.Left),
            (int)Math.Round(selection.Top),
            (int)Math.Round(selection.Right),
            (int)Math.Round(selection.Bottom));
        try
        {
            _preview = ScreenshotComposer.Compose(_desktop.Bitmap, crop, _edit.Annotations);
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
        try
        {
            var crop = new SKRectI(
                (int)Math.Round(selection.Left), (int)Math.Round(selection.Top),
                (int)Math.Round(selection.Right), (int)Math.Round(selection.Bottom));
            output = ScreenshotComposer.Compose(_desktop.Bitmap, crop, _edit.Annotations);
            _completion.TrySetResult(SaveAndCopy(output, _settings, width, height));
        }
        catch (Exception ex)
        {
            AppLog.Error("截图处理失败", ex);
            _completion.TrySetResult(new ScreenshotCaptureResult(
                null, width, height, false, $"截图处理失败：{ex.Message}", false));
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>Saves one composed bitmap and copies it to the clipboard; shared by every
    /// capture mode (region, full screen).</summary>
    internal static ScreenshotCaptureResult SaveAndCopy(
        SKBitmap output, AppSettings settings, int width, int height)
    {
        string? savedPath = null;
        string? saveError = null;
        string? copyError = null;
        bool copied = false;

        if (settings.ScreenshotAutoSave)
        {
            try
            {
                string directory = ScreenshotNaming.ResolveDirectory(settings);
                Directory.CreateDirectory(directory);

                // 编码格式/质量来自设置（默认 PNG 无损，可选 WebP/JPEG）；编码失败会自动
                // 回退 PNG 并把原因写进 saveError 告知用户——不静默换格式。
                ImageEncodeResult encoded = ImageEncoderService.Default.Encode(
                    output,
                    ImageExportFormats.ParseOrDefault(settings.ScreenshotImageFormat),
                    settings.ScreenshotImageQuality);
                string fileName = ScreenshotNaming.EnsureUnique(
                    directory, ScreenshotNaming.BuildFileName(DateTime.Now, encoded.FileExtension));
                savedPath = Path.Combine(directory, fileName);
                File.WriteAllBytes(savedPath, encoded.Bytes);

                if (encoded.Note is { Length: > 0 } note)
                {
                    saveError = note;
                    AppLog.Warn($"截图编码回退：{note}");
                }
            }
            catch (Exception ex)
            {
                saveError = $"保存失败：{ex.Message}";
                savedPath = null;
                AppLog.Error("截图保存失败", ex);
            }
        }

        if (settings.ScreenshotAutoCopy)
        {
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

        if (!settings.ScreenshotAutoSave && !settings.ScreenshotAutoCopy)
            saveError = "已关闭自动复制和自动保存，本次截图没有输出";

        string? error = (saveError, copyError) switch
        {
            (null, null) => null,
            (not null, null) => saveError,
            (null, not null) => copyError,
            _ => $"{saveError}；{copyError}",
        };

        return new ScreenshotCaptureResult(savedPath, width, height, copied, error, false);
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
