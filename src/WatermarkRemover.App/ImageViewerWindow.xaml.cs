using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App;

/// <summary>
/// The in-app image viewer (看图). One reusable window: wheel zooms around the cursor,
/// drag pans, double-click flips between fit and 100%, R rotates the view, arrow keys walk
/// the folder, F11 goes fullscreen and shows a transparency checkerboard behind the photo.
/// Formats WPF cannot decode (WebP/AVIF/HEIC) fall back to the SkiaSharp decoder the screenshot
/// pipeline already uses, and JPEG EXIF orientation is applied so portrait shots are upright.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico",
        ".webp", ".avif", ".heic", ".heif",
    };

    private const double MinZoom = 0.04;
    private const double MaxZoom = 24;

    private static ImageViewerWindow? _current;

    private string _path = string.Empty;
    private BitmapSource? _image;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private double _angle;
    private ViewerMode _mode = ViewerMode.Fit;
    private string _fileSize = string.Empty;
    private readonly List<string> _siblings = new();
    private int _siblingIndex = -1;

    private bool _panning;
    private Point _panStart;
    private double _panFromX;
    private double _panFromY;

    private bool _fullscreen;
    private WindowState _restoreState = WindowState.Normal;
    private Rect _restoreBounds;
    private bool _restoreTopmost;
    private string? _toastText;

    private ImageViewerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
        Closed += (_, _) =>
        {
            if (_current == this) _current = null;
        };
    }

    /// <summary>Opens <paramref name="path"/> in the single viewer window and brings it forward.</summary>
    public static void ShowFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        if (_current is { } existing)
        {
            existing.Load(path);
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new ImageViewerWindow { WindowStartupLocation = WindowStartupLocation.Manual };
        _current = window;
        window.Load(path);
        window.Show();
        window.Activate();
    }

    // --- Loading ----------------------------------------------------------------

    private void Load(string path)
    {
        BitmapSource? decoded = Decode(path, out int orientation);
        if (decoded is null)
        {
            StatusHint.Text = $"无法读取：{Path.GetFileName(path)}";
            StatusHint.Visibility = Visibility.Visible;
            return;
        }

        _path = path;
        _image = ApplyOrientation(decoded, orientation);
        _pixelWidth = _image.PixelWidth;
        _pixelHeight = _image.PixelHeight;
        Preview.Source = _image;
        Preview.Width = _pixelWidth;
        Preview.Height = _pixelHeight;
        Spin.CenterX = _pixelWidth / 2.0;
        Spin.CenterY = _pixelHeight / 2.0;

        _angle = 0;
        _scale = 1;
        _offsetX = _offsetY = 0;
        _mode = ViewerMode.Fit;
        StatusHint.Visibility = Visibility.Collapsed;
        _toastText = null;

        try
        {
            long bytes = new FileInfo(path).Length;
            _fileSize = bytes >= 1024 * 1024
                ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
                : $"{Math.Max(1, bytes / 1024)} KB";
        }
        catch
        {
            _fileSize = string.Empty;
        }

        TitleText.Text = Path.GetFileName(path);
        Title = $"看图 · {Path.GetFileName(path)}";

        RefreshSiblings();
        SizeWindowForImage();
        Apply();
    }

    private static BitmapSource? Decode(string path, out int orientation)
    {
        orientation = 1;
        try
        {
            var decoder = BitmapDecoder.Create(
                new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            orientation = ReadOrientation(frame);
            if (!frame.IsFrozen) frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            // No WPF codec for WebP/AVIF/HEIC and some camera raws: SkiaSharp reads them.
            try
            {
                using SKBitmap? bitmap = SKBitmap.Decode(path);
                if (bitmap is not null)
                {
                    orientation = 1;
                    return SkiaBitmapBridge.ToBitmapSource(bitmap);
                }
            }
            catch (Exception inner)
            {
                AppLog.Warn($"看图解码失败：{path}（{inner.Message}）");
            }

            AppLog.Warn($"看图解码失败：{path}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>EXIF tag 274: 3/6/8 are the 180°/90°/270° cases phones and cameras write.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value)
            {
                return value;
            }
        }
        catch
        {
            // PNG/BMP carry no EXIF block; not an error.
        }

        return 1;
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        double angle = orientation switch
        {
            3 => 180,
            6 => 90,
            8 => 270,
            _ => 0,
        };
        if (angle == 0) return source;

        try
        {
            var rotated = new TransformedBitmap(source, new RotateTransform(angle));
            rotated.Freeze();
            return rotated;
        }
        catch
        {
            return source;
        }
    }

    private void RefreshSiblings()
    {
        _siblings.Clear();
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                _siblings.AddRange(Directory.EnumerateFiles(directory)
                    .Where(IsImageFile)
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            AppLog.Trace("看图读取同目录图片失败", ex);
        }

        _siblingIndex = _siblings.FindIndex(
            f => string.Equals(f, _path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Opens at the image's own size, capped to 85% of the work area.</summary>
    private void SizeWindowForImage()
    {
        if (_pixelWidth <= 0 || _pixelHeight <= 0) return;

        Rect work = SystemParameters.WorkArea;
        const double chromeHeight = 62; // header 36 + footer 26
        double scale = Math.Min(1,
            Math.Min(work.Width * 0.85 / _pixelWidth,
                (work.Height * 0.85 - chromeHeight) / _pixelHeight));

        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Width = Math.Max(MinWidth, _pixelWidth * scale);
        Height = Math.Max(MinHeight, _pixelHeight * scale + chromeHeight);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    // --- Layout, zoom and pan ---------------------------------------------------

    private double DisplayWidth => ((int)_angle & 180) == 0 ? _pixelWidth : _pixelHeight;

    private double DisplayHeight => ((int)_angle & 180) == 0 ? _pixelHeight : _pixelWidth;

    private void Apply()
    {
        if (_image is null) return;

        double viewWidth = Viewport.ActualWidth;
        double viewHeight = Viewport.ActualHeight;
        if (viewWidth <= 0 || viewHeight <= 0) return;

        if (_mode == ViewerMode.Fit)
        {
            // Fit never enlarges small images past their own pixels.
            _scale = Math.Min(1, Math.Min(viewWidth / DisplayWidth, viewHeight / DisplayHeight));
            _offsetX = 0;
            _offsetY = 0;
        }
        else if (_mode == ViewerMode.Actual)
        {
            _scale = 1;
            _offsetX = 0;
            _offsetY = 0;
        }

        ClampPan(viewWidth, viewHeight);

        Spin.Angle = _angle;
        Zoom.ScaleX = _scale;
        Zoom.ScaleY = _scale;
        Pan.X = viewWidth / 2 - _scale * _pixelWidth / 2 + _offsetX;
        Pan.Y = viewHeight / 2 - _scale * _pixelHeight / 2 + _offsetY;

        UpdateChrome();
    }

    private void ClampPan(double viewWidth, double viewHeight)
    {
        double shownWidth = DisplayWidth * _scale;
        double shownHeight = DisplayHeight * _scale;
        double limitX = Math.Max(0, (shownWidth - viewWidth) / 2);
        double limitY = Math.Max(0, (shownHeight - viewHeight) / 2);
        _offsetX = Math.Clamp(_offsetX, -limitX, limitX);
        _offsetY = Math.Clamp(_offsetY, -limitY, limitY);
    }

    private void ZoomAt(Point cursor, double factor)
    {
        if (_image is null) return;

        double target = Math.Clamp(_scale * factor, MinZoom, MaxZoom);
        double change = target / _scale;
        if (Math.Abs(change - 1) < 0.001) return;

        // Keep the image pixel under the cursor pinned while the scale changes.
        double centerX = Viewport.ActualWidth / 2 + _offsetX;
        double centerY = Viewport.ActualHeight / 2 + _offsetY;
        _offsetX += (cursor.X - centerX) * (1 - change);
        _offsetY += (cursor.Y - centerY) * (1 - change);
        _scale = target;
        _mode = ViewerMode.Free;
        Apply();
    }

    private void ZoomCenter(double factor) =>
        ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), factor);

    private void UpdateChrome()
    {
        string modeText = _mode switch
        {
            ViewerMode.Fit => "适应窗口",
            ViewerMode.Actual => "原始大小",
            _ => "自由缩放",
        };
        string rotation = _angle == 0 ? string.Empty : $" · 已旋转 {_angle:0}°";
        string position = _siblings.Count > 1
            ? $" · {Math.Max(1, _siblingIndex + 1)}/{_siblings.Count}"
            : string.Empty;
        string info = _toastText ?? $"{_pixelWidth}×{_pixelHeight} · {_fileSize} · {modeText}{rotation}{position}";
        InfoText.Text = info.Trim(' ', '·');
        InfoText.Foreground = _toastText is null
            ? new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB2))
            : new SolidColorBrush(Color.FromRgb(0x8F, 0xD6, 0x9A));
        ZoomText.Text = $"{Math.Round(_scale * 100)}%";
    }

    // --- Header actions ----------------------------------------------------------

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        _mode = ViewerMode.Fit;
        Apply();
    }

    private void OnActualClick(object sender, RoutedEventArgs e)
    {
        _mode = ViewerMode.Actual;
        Apply();
    }

    private void OnRotateClick(object sender, RoutedEventArgs e) => Rotate(90);

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyImage();

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"定位图片失败：{ex.Message}");
        }
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Rotate(double degrees)
    {
        _angle = (_angle + degrees + 360) % 360;
        _offsetX = 0;
        _offsetY = 0;
        Apply();
    }

    private void CopyImage()
    {
        if (_image is null) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(_image);
                Toast("已复制到剪贴板");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2) AppLog.Warn($"看图复制失败：{ex.Message}");
                Thread.Sleep(40);
            }
        }
    }

    private void Toast(string text)
    {
        _toastText = text;
        UpdateChrome();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1300),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toastText = null;
            UpdateChrome();
        };
        timer.Start();
    }

    // --- Mouse -------------------------------------------------------------------

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Button released before the drag loop started.
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_image is null) return;

        if (e.ClickCount >= 2)
        {
            _mode = _mode == ViewerMode.Fit ? ViewerMode.Actual : ViewerMode.Fit;
            Apply();
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panFromX = _offsetX;
        _panFromY = _offsetY;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;

        Point position = e.GetPosition(Viewport);
        _offsetX = _panFromX + (position.X - _panStart.X);
        _offsetY = _panFromY + (position.Y - _panStart.Y);
        if (_mode == ViewerMode.Actual) _mode = ViewerMode.Free;
        Apply();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        EndPan();
        e.Handled = true;
    }

    private void OnViewportMouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning && e.LeftButton == MouseButtonState.Released) EndPan();
    }

    private void EndPan()
    {
        _panning = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => Apply();

    private void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image is null) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1.06 : 1.15;
        ZoomAt(e.GetPosition(Viewport), e.Delta > 0 ? step : 1 / step);
        e.Handled = true;
    }

    // --- Keyboard ----------------------------------------------------------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.Escape:
                if (_fullscreen) ToggleFullscreen();
                else Close();
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Left:
                StepImage(-1);
                break;
            case Key.Right:
                StepImage(1);
                break;
            case Key.R:
                Rotate(shift ? -90 : 90);
                break;
            case Key.D0:
            case Key.NumPad0:
                _mode = ViewerMode.Fit;
                Apply();
                break;
            case Key.D1:
            case Key.NumPad1:
                _mode = ViewerMode.Actual;
                Apply();
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomCenter(1.15);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomCenter(1 / 1.15);
                break;
            case Key.C when ctrl:
                CopyImage();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void StepImage(int delta)
    {
        if (_siblings.Count <= 1) return;
        int next = _siblingIndex + delta;
        if (next < 0 || next >= _siblings.Count) return;
        Load(_siblings[next]);
    }

    // --- Fullscreen --------------------------------------------------------------

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            _restoreBounds = new Rect(Left, Top, Width, Height);
            _restoreTopmost = Topmost;
            _fullscreen = true;
            Header.Visibility = Visibility.Collapsed;
            HeaderRow.Height = new GridLength(0);
            FooterRow.Height = new GridLength(0);

            if (TryCoverMonitor())
            {
                Topmost = true;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            Apply();
            return;
        }

        _fullscreen = false;
        Header.Visibility = Visibility.Visible;
        HeaderRow.Height = new GridLength(36);
        FooterRow.Height = new GridLength(26);
        WindowState = WindowState.Normal;
        Topmost = _restoreTopmost;
        Left = _restoreBounds.Left;
        Top = _restoreBounds.Top;
        Width = _restoreBounds.Width;
        Height = _restoreBounds.Height;
        WindowState = _restoreState;
        Apply();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>True fullscreen: the monitor rectangle, taskbar included, converted to DIPs.</summary>
    private bool TryCoverMonitor()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return false;

            IntPtr monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

            Matrix toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                           ?? Matrix.Identity;
            Point topLeft = toDip.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
            Point bottomRight = toDip.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));

            WindowState = WindowState.Normal;
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = Math.Max(MinWidth, bottomRight.X - topLeft.X);
            Height = Math.Max(MinHeight, bottomRight.Y - topLeft.Y);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Trace("全屏定位失败，退回最大化", ex);
            return false;
        }
    }

    private enum ViewerMode
    {
        Fit,
        Actual,
        Free,
    }
}
