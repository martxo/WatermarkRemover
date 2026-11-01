using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App;

/// <summary>
/// Fullscreen screenshot viewer. It is the same shell as FullscreenViewerWindow (PDF pages):
/// the whole screen covered by a dark veil, a caption bar with title/position/actions, and the
/// shared <see cref="Controls.ImageSurface"/> in the middle — so a thumbnail click feels
/// identical no matter which kind of record it belongs to.
/// Formats WPF cannot decode (WebP/AVIF/HEIC) fall back to SkiaSharp, and JPEG EXIF orientation
/// is applied so portrait shots are upright. Wheel/drag/double-click live in the surface.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico",
        ".webp", ".avif", ".heic", ".heif",
    };

    private static ImageViewerWindow? _current;

    private string _path = string.Empty;
    private BitmapSource? _image;
    private string _fileSize = string.Empty;
    private readonly List<string> _siblings = new();
    private int _siblingIndex = -1;

    private ImageViewerWindow()
    {
        InitializeComponent();
    }

    /// <summary>Opens <paramref name="path"/> in the single viewer window and brings it forward.</summary>
    public static void ShowFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        if (_current is { } existing)
        {
            existing.Load(path);
            existing.RestoreAndActivate();
            return;
        }

        var window = new ImageViewerWindow();
        _current = window;
        window.Closed += (_, _) =>
        {
            if (_current == window) _current = null;
        };
        window.Load(path);
        window.Show();
        window.Activate();
    }

    private void RestoreAndActivate()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        if (!IsActive) Activate();
    }

    // --- Loading ----------------------------------------------------------------

    private void Load(string path)
    {
        BitmapSource? decoded = Decode(path, out int orientation);
        if (decoded is null)
        {
            Surface.Source = null;
            Surface.ShowStatus($"无法读取：{Path.GetFileName(path)}");
            return;
        }

        _path = path;
        _image = ApplyOrientation(decoded, orientation);
        Surface.ShowStatus(null);
        Surface.Source = _image;

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
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        PositionText.Text = _siblings.Count > 1
            ? $"{Math.Max(1, _siblingIndex + 1)}/{_siblings.Count}"
            : string.Empty;
        if (_image is null)
        {
            InfoText.Text = string.Empty;
            return;
        }

        InfoText.Text = $"{_image.PixelWidth}×{_image.PixelHeight} · {_fileSize} · " +
                        $"{Surface.ZoomPercent:0}% · {Surface.ModeName}";
    }

    private void OnSurfaceChanged(object? sender, EventArgs e) => UpdateCaption();

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

    // --- Caption actions ----------------------------------------------------------

    private void OnRotateClick(object sender, RoutedEventArgs e) => Surface.Rotate(90);

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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void CopyImage()
    {
        if (_image is null) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(_image);
                ToastText.Text = "已复制到剪贴板";
                Toast.Visibility = Visibility.Visible;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1300),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Toast.Visibility = Visibility.Collapsed;
                };
                timer.Start();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2) AppLog.Warn($"看图复制失败：{ex.Message}");
                Thread.Sleep(40);
            }
        }
    }

    // --- Keyboard ----------------------------------------------------------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                StepImage(-1);
                break;
            case Key.Right:
                StepImage(1);
                break;
            case Key.R:
                Surface.Rotate(shift ? -90 : 90);
                break;
            case Key.D0:
            case Key.NumPad0:
                Surface.Fit();
                break;
            case Key.D1:
            case Key.NumPad1:
                Surface.ActualSize();
                break;
            case Key.OemPlus:
            case Key.Add:
                Surface.ZoomAtCenter(1.15);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                Surface.ZoomAtCenter(1 / 1.15);
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
}
