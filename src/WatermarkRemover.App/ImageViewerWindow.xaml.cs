using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using WatermarkRemover.App.Controls;
using WatermarkRemover.App.Editing;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Imaging.Encoding;
using WatermarkRemover.Imaging.Screenshot;

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

    // --- 标注（阶段 1：AnnotationEditCore 的第二个宿主）---------------------------
    // 画布坐标 = 原图像素（EXIF 方向已应用，与屏幕上看到的一致）；粗细/字号按当前缩放
    // 换算回图像素。RequireBothSides 与标注窗一致（false）；截图侧语义用户决策中，不动。
    private readonly AnnotationEditSession _edit = new()
    {
        StrokeFloor = 1f,
        MinMarkSpan = 3f,
        MinPenStep = 1f,
        RequireBothSides = false,
    };

    /// <summary>覆盖层（草稿层 + 文字浮层），编辑时挂到 ImageSurface 的覆盖层挂载点。</summary>
    private readonly AnnotationOverlay _overlay = new();

    /// <summary>标注合成底图：与当前显示图同尺寸（原图不动，标注只烧进另存的副本）。</summary>
    private SKBitmap? _editBase;

    /// <summary>截图导出设置（格式/质量）：标注副本按它编码，默认 PNG。</summary>
    private AppSettings _settings = new();

    private bool _editing;

    private ImageViewerWindow()
    {
        InitializeComponent();
        RailList.ItemsSource = _rail;
        WireAnnotation();
        // 布局稳定后再适应一次：窗口尺寸在 Show 之后才最终落定，避免按中间尺寸缩放。
        Loaded += (_, _) => Surface.Fit();
    }

    /// <summary>Opens <paramref name="path"/> in the single viewer window and brings it forward.</summary>
    public static void ShowFor(string path, AppSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        if (_current is { } existing)
        {
            existing._settings = settings ?? existing._settings;
            existing.Load(path);
            existing.RestoreAndActivate();
            return;
        }

        var window = new ImageViewerWindow { _settings = settings ?? new AppSettings() };
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
        // 复用本窗换图（外部 ShowFor / 胶片轨 / 翻页 / 保存副本后切到副本）：标注绑定的是
        // 上一张图，先按需退出（有未保存标注时会确认，保存副本前已清空所以不打断流程）。
        if (_editing && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            ExitEdit();

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
        if (_editing)
        {
            ShowToast("请先保存或退出标注，再切换图片");
            return;
        }

        if (!string.Equals(item.Path, _path, StringComparison.OrdinalIgnoreCase))
            Load(item.Path);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => StepImage(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => StepImage(1);

    // --- Caption actions ----------------------------------------------------------

    private void OnRotateClick(object sender, RoutedEventArgs e) => Surface.Rotate(90);

    /// <summary>
    /// 进入/退出标注（「标注」按钮与 M 键）。与旧的「另开标注窗」相比，编辑直接长在查看器里：
    /// 工具条浮在画布顶部，草稿层叠在图片平面上，保存仍写同目录副本、原图不动。
    /// </summary>
    private void OnMarkupClick(object sender, RoutedEventArgs e) => ToggleEdit();

    private void ToggleEdit()
    {
        if (_editing) ExitEdit();
        else EnterEdit();
    }

    /// <summary>
    /// 把标注会话接到覆盖层与工具条上。全部逻辑在 <see cref="AnnotationEditSession"/> 里，
    /// 这里只做三件宿主该做的事：注入坐标换算、转发工具条事件、把会话变化翻译成界面更新。
    /// 新工具注册进注册表后，工具条按钮与快捷键自动可用，本方法无需改动。
    /// </summary>
    private void WireAnnotation()
    {
        _overlay.Session = _edit;
        _overlay.ToCanvas = plane => Surface.PlaneToPixel(plane);
        _overlay.ToPlane = pixel => Surface.PixelToPlane(pixel);

        Toolbar.ToolClick += (_, tool) => _edit.SelectTool(tool);
        Toolbar.ColorClick += (_, argb) => _edit.SetColor(argb);
        Toolbar.WidthClick += (_, dip) => _edit.SetLineWidth(dip);
        Toolbar.FontSizeClick += (_, size) => _edit.SetTextFontSize(size);
        Toolbar.UndoClick += (_, _) => _edit.Undo();
        Toolbar.RedoClick += (_, _) => _edit.Redo();
        Toolbar.ClearClick += (_, _) => _edit.Clear();

        // 画布坐标 = 原图像素；粗细/字号按当前显示缩放换算回图像素（DIP ÷ 缩放）。
        _edit.ScaleAt = _ => 1f / (float)Surface.Scale;
        _edit.CanPlaceText = point => _image is not null
            && point.X >= 0 && point.Y >= 0
            && point.X <= _image.PixelWidth && point.Y <= _image.PixelHeight;

        // 与标注窗一致的通知分工：历史变化才重算合成预览，样式变化只刷工具条/光标。
        _edit.HistoryChanged += (_, _) =>
        {
            RefreshAnnotatedPreview();
            UpdateEditChrome();
        };
        _edit.StyleChanged += (_, _) =>
        {
            UpdateEditChrome();
            _overlay.UpdateCursor();
            // 选了工具才抢占指针；取消选择后平移/缩放立刻恢复（覆盖层透传回平面）。
            Surface.OverlayInteractive = _edit.Tool != ScreenshotTool.None;
        };

        UpdateEditChrome();
    }

    private void EnterEdit()
    {
        if (_editing || _image is null) return;

        // 合成底图取「当前显示的图」（EXIF 方向已应用），画布坐标才和屏幕对得上。
        if (SkiaBitmapBridge.FromBitmapSource(_image) is not { } baseBitmap)
        {
            ShowToast("这张图片的格式暂不支持标注");
            return;
        }

        _editBase?.Dispose();
        _editBase = baseBitmap;
        _edit.Clear();
        _editing = true;

        _overlay.Visibility = Visibility.Visible;
        Surface.Overlay = _overlay;
        Surface.OverlayInteractive = false; // 未选工具时不抢占，滚轮/拖动照旧
        _overlay.UpdateCursor();

        EditBar.Visibility = Visibility.Visible;
        EditHint.Visibility = Visibility.Visible;
        MarkupButton.Content = "退出标注";
        UpdateEditChrome();
        AppLog.Info($"看图进入标注：{Path.GetFileName(_path)}");
    }

    private void ExitEdit()
    {
        if (!_editing) return;

        if (_edit.HasAnnotations && !ConfirmDiscardAnnotations()) return;

        _editing = false;
        // 先清会话：预览随之回到原图像素（原文件本来就没动过），再销毁底图。
        _edit.Clear();
        _editBase?.Dispose();
        _editBase = null;

        Surface.Overlay = null;
        _overlay.Visibility = Visibility.Collapsed;
        EditBar.Visibility = Visibility.Collapsed;
        EditHint.Visibility = Visibility.Collapsed;
        MarkupButton.Content = "标注";
        UpdateEditChrome();
    }

    private void OnExitEditClick(object sender, RoutedEventArgs e) => ExitEdit();

    /// <summary>放弃未保存标注的确认。全屏置顶窗会压住对话框，先临时让出 Topmost。</summary>
    private bool ConfirmDiscardAnnotations()
    {
        bool wasTopmost = Topmost;
        Topmost = false;
        try
        {
            return MessageBox.Show(
                this,
                "当前图片有未保存的标注，放弃并退出吗？\n（磁盘上的原图不会被修改）",
                "标注",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) == MessageBoxResult.OK;
        }
        finally
        {
            Topmost = wasTopmost;
        }
    }

    // --- 标注：预览 / 保存 --------------------------------------------------------

    /// <summary>重算「原图 + 标注」的合成预览。铁律：预览与保存同走 ScreenshotComposer，
    /// 屏幕上看到的（1:1 下）就是副本里的内容；ReplaceSource 保留当前缩放/平移不重置视角。</summary>
    private void RefreshAnnotatedPreview()
    {
        if (_editBase is null) return;

        if (_edit.Annotations.Count == 0)
        {
            Surface.ReplaceSource(_image);
            return;
        }

        using SKBitmap composed = ComposeAnnotations();
        Surface.ReplaceSource(SkiaBitmapBridge.ToBitmapSource(composed));
    }

    private SKBitmap ComposeAnnotations() => ScreenshotComposer.Compose(
        _editBase!, new SKRectI(0, 0, _editBase!.Width, _editBase.Height), _edit.Annotations);

    private void OnSaveCopyClick(object sender, RoutedEventArgs e) => SaveCopy();

    /// <summary>
    /// 把标注烧进图片，另存为同目录「原名_标注_HHmmss.png」副本（原图不动），保存后直接看
    /// 这份副本——屏幕上看到的永远是磁盘上的文件，并可接着在副本上继续标注。
    /// </summary>
    private void SaveCopy()
    {
        if (_editBase is null || _edit.Annotations.Count == 0)
        {
            ShowToast("请先添加标注再保存");
            return;
        }

        try
        {
            using SKBitmap composed = ComposeAnnotations();
            string directory = Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            string stem = Path.GetFileNameWithoutExtension(_path);

            // 导出格式/质量跟随截图设置（默认 PNG；WebP/JPEG 见设置页），失败自动回退 PNG。
            ImageEncodeResult encoded = ImageEncoderService.Default.Encode(
                composed,
                ImageExportFormats.ParseOrDefault(_settings.ScreenshotImageFormat),
                _settings.ScreenshotImageQuality);
            string fileName = ScreenshotNaming.EnsureUnique(
                directory, $"{stem}_标注_{DateTime.Now:HHmmss}{encoded.FileExtension}");
            string target = Path.Combine(directory, fileName);
            File.WriteAllBytes(target, encoded.Bytes);

            string saved = target;
            // 副本已含全部标注：清会话避免二次叠加，然后切到副本（胶片轨/标题随之刷新，
            // Load 里的换图守卫会顺带退出标注态）。
            _edit.Clear();
            Load(saved);
            ShowToast(encoded.Note is { Length: > 0 } note
                ? note
                : $"已标注并保存副本：{Path.GetFileName(saved)}");
            AppLog.Info($"看图标注已保存：{saved}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"看图标注保存失败：{ex.Message}");
            ShowToast($"保存失败：{ex.Message}");
        }
    }

    private void UpdateEditChrome()
    {
        Toolbar.Sync(
            _edit.Tool,
            _edit.Style.ColorArgb,
            _edit.Style.LineWidthDip,
            _edit.Style.TextFontSizeDip,
            _edit.HasAnnotations,
            _edit.CanRedo,
            _edit.HasAnnotations);

        EditStatusText.Text = _edit.HasAnnotations
            ? $"{_edit.Annotations.Count} 个标注 · 未保存"
            : "选工具后在图上绘制";
        EditHintText.Text = "矩形/箭头/画笔/马赛克拖拽绘制 · 文字点击输入 · " +
                            "Ctrl+Z 撤销 · Ctrl+Y 重做 · Ctrl+S 保存副本 · Esc 退出标注";
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
        // 编辑中：复制「屏幕上看到的」（原图 + 标注），与保存副本同一合成器。
        if (_editing && _editBase is not null && _edit.Annotations.Count > 0)
        {
            try
            {
                using SKBitmap composed = ComposeAnnotations();
                if (TrySetClipboard(SkiaBitmapBridge.ToBitmapSource(composed)))
                {
                    ShowToast("已复制标注后的图片");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"看图复制标注图失败：{ex.Message}");
            }
        }

        if (_image is null) return;
        if (TrySetClipboard(_image)) ShowToast("已复制到剪贴板");
    }

    /// <summary>剪贴板偶发被其他进程占用：AppClipboard 带退避重试，彻底失败返回 false。</summary>
    private static bool TrySetClipboard(BitmapSource image)
    {
        try
        {
            AppClipboard.SetImage(image);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"看图复制失败：{ex.Message}");
            return false;
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
        if (_editing)
        {
            OnEditKeyDown(e);
            return;
        }

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
                EnterEdit();
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

    /// <summary>
    /// 编辑态按键：工具快捷键直接查 <see cref="AnnotationToolRegistry"/>（新工具注册后自动
    /// 支持，R/A/P/T/M 在此分别=矩形/箭头/画笔/文字/马赛克）；Esc 先收工具再退出标注；
    /// 翻页让位（标注属于当前图，换图必须先保存或放弃）；视图键与复制保留。
    /// </summary>
    private void OnEditKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.Escape:
                if (_edit.Tool != ScreenshotTool.None) _edit.SelectTool(ScreenshotTool.None);
                else ExitEdit();
                break;
            case Key.Z when ctrl:
                _edit.Undo();
                break;
            case Key.Y when ctrl:
                _edit.Redo();
                break;
            case Key.S when ctrl:
                SaveCopy();
                break;
            case Key.C when ctrl:
                CopyImage();
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
            case Key.Left:
            case Key.Right:
                ShowToast("请先保存或退出标注，再切换图片");
                break;
            default:
                if (ctrl || AnnotationToolRegistry.Default.FindByKey(e.Key) is not { } tool) return;
                _edit.SelectTool(tool.Tool);
                break;
        }

        e.Handled = true;
    }

    private void StepImage(int delta)
    {
        if (_siblings.Count <= 1) return;
        if (_editing)
        {
            ShowToast("请先保存或退出标注，再切换图片");
            return;
        }

        int next = _siblingIndex + delta;
        if (next < 0 || next >= _siblings.Count) return;
        Load(_siblings[next]);
    }

    /// <summary>关闭前拦下未保存标注（原图不会被修改，但屏幕上的标注会丢）。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_editing && _edit.HasAnnotations && !ConfirmDiscardAnnotations())
        {
            e.Cancel = true;
            return;
        }

        _editBase?.Dispose();
        _editBase = null;
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
