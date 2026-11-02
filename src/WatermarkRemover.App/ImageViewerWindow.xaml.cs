using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App;

/// <summary>
/// Fullscreen screenshot viewer. It is the same shell as FullscreenViewerWindow (PDF pages):
/// the whole screen covered by a dark veil, a film rail of the folder's images on the left,
/// a caption bar with title/position/actions, paging arrows on the canvas edges, and the shared
/// <see cref="Controls.ImageSurface"/> in the middle — so a thumbnail click feels identical no
/// matter which kind of record it belongs to.
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

    private readonly ObservableCollection<RailItem> _rail = new();
    private DispatcherTimer? _toastTimer;
    private string? _railDirectory;
    private int _railGeneration;

    private ImageViewerWindow()
    {
        InitializeComponent();
        RailList.ItemsSource = _rail;
        // 布局稳定后再适应一次：窗口尺寸在 Show 之后才最终落定，避免按中间尺寸缩放。
        Loaded += (_, _) => Surface.Fit();
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
        // 统一由 ScreenCoverage 铺满显示器：WindowState=Maximized 的无边框窗口会停在任务栏外，
        // 底部露出桌面，看图内容和 PDF 看图一样会"少一截"。
        ScreenCoverage.CoverScreenOf(this);
        if (!IsActive) Activate();
    }

    /// <summary>创建即铺满显示器物理边界（含任务栏那条），见服务里的说明。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ScreenCoverage.CoverScreenOf(this);
        DpiChanged += (_, _) => ScreenCoverage.CoverScreenOf(this);
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
        SyncRail();
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        PositionText.Text = _siblings.Count > 1
            ? $"{Math.Max(1, _siblingIndex + 1)}/{_siblings.Count}"
            : string.Empty;
        PrevButton.IsEnabled = _siblingIndex > 0;
        NextButton.IsEnabled = _siblingIndex >= 0 && _siblingIndex < _siblings.Count - 1;

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

    // --- Film rail ---------------------------------------------------------------

    /// <summary>
    /// Keeps the left rail in sync with the folder: it is rebuilt only when the directory
    /// changes; paging inside one folder just moves the highlight, so no re-decoding.
    /// </summary>
    private void SyncRail()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.Equals(directory, _railDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _railDirectory = directory;
            _railGeneration++;
            _rail.Clear();
            foreach (string file in _siblings)
                _rail.Add(new RailItem(file));
            DecodeRailThumbnails(_railGeneration);
        }

        for (int i = 0; i < _rail.Count; i++)
            _rail[i].IsCurrent = i == _siblingIndex;

        // The list is filled before it is measured, so scroll on the next layout pass.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollRailToCurrent));
    }

    private void ScrollRailToCurrent()
    {
        if (_siblingIndex < 0 || _siblingIndex >= _rail.Count) return;
        RailList.UpdateLayout();
        if (RailList.ItemContainerGenerator.ContainerFromIndex(_siblingIndex) is FrameworkElement container)
            container.BringIntoView();
    }

    /// <summary>Thumbnails decode off the UI thread, one at a time, so big folders stay fluid.</summary>
    private async void DecodeRailThumbnails(int generation)
    {
        foreach (RailItem item in _rail.ToList())
        {
            if (generation != _railGeneration) return;
            ImageSource? thumb = await Task.Run(() => DecodeThumb(item.Path));
            if (generation != _railGeneration) return;
            item.Thumb = thumb;
        }
    }

    /// <summary>Small frozen preview; formats WPF cannot read fall back to SkiaSharp.</summary>
    private static ImageSource? DecodeThumb(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 152;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // No WPF codec for this file; try SkiaSharp below.
        }

        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(path);
            if (bitmap is null) return null;
            const int width = 152;
            int height = Math.Max(1, (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width)));
            using SKBitmap? resized = bitmap.Resize(
                new SKImageInfo(width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            return resized is null ? null : SkiaBitmapBridge.ToBitmapSource(resized);
        }
        catch (Exception ex)
        {
            AppLog.Trace("看图缩略图解码失败", ex);
            return null;
        }
    }

    private void OnRailThumbClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RailItem item }) return;
        e.Handled = true;
        if (!string.Equals(item.Path, _path, StringComparison.OrdinalIgnoreCase))
            Load(item.Path);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => StepImage(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => StepImage(1);

    // --- Caption actions ----------------------------------------------------------

    private void OnRotateClick(object sender, RoutedEventArgs e) => Surface.Rotate(90);

    /// <summary>
    /// 标注/打码：复用截图用的标注编辑器（矩形/箭头/画笔/文字/马赛克 + 撤销重做）。它把结果
    /// 写成同目录的「原名_标注_时间.png」副本，原图永远不动；保存后把新副本收进左侧胶片轨并
    /// 提示文件名，方便接着看。
    /// </summary>
    private void OnMarkupClick(object sender, RoutedEventArgs e) => OpenMarkup();

    private void OpenMarkup()
    {
        if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;

        try
        {
            var editor = new ImageMarkupWindow(_path, (target, width, height) =>
            {
                AppLog.Info($"图片标注已保存：{target}（{width}×{height}）");
                RefreshSiblings();
                SyncRail();
                ShowToast($"已标注并保存副本：{Path.GetFileName(target)}");
            });
            editor.Show();
            editor.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开标注窗口失败：{ex.Message}");
            ShowToast($"无法打开标注窗口：{ex.Message}");
        }
    }

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
                ShowToast("已复制到剪贴板");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2) AppLog.Warn($"看图复制失败：{ex.Message}");
                Thread.Sleep(40);
            }
        }
    }

    /// <summary>Bottom-centre toast. The timer is kept on the window so a second message replaces
    /// the first one's expiry instead of leaving two hidden timers racing.</summary>
    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _toastTimer = timer;
        timer.Start();
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
            case Key.M:
                OpenMarkup();
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

    /// <summary>One tile in the left rail: the file behind it, its preview, and whether it is
    /// the image currently on the canvas.</summary>
    public sealed class RailItem : INotifyPropertyChanged
    {
        private ImageSource? _thumb;
        private bool _isCurrent;

        public RailItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
        }

        public string Path { get; }

        public string Name { get; }

        public ImageSource? Thumb
        {
            get => _thumb;
            set
            {
                if (ReferenceEquals(_thumb, value)) return;
                _thumb = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
            }
        }

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
