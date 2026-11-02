using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Controls;

/// <summary>
/// One picture plane shared by every in-app viewer: the screenshot viewer and the PDF page
/// viewer. Wheel zooms around the cursor, drag pans, double click toggles fit and 100%,
/// and the view can be rotated without touching the file. Hosts render the chrome (caption,
/// paging, close) and only read <see cref="ZoomPercent"/> / <see cref="ModeName"/> back.
/// </summary>
public partial class ImageSurface : UserControl
{
    private const double MinZoom = 0.04;
    private const double MaxZoom = 24;

    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private double _angle;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _fit = true;

    private bool _panning;
    private Point _panStart;
    private double _panFromX;
    private double _panFromY;

    public ImageSurface()
    {
        InitializeComponent();
    }

    /// <summary>Raised after every fit / zoom / pan so the host can refresh its caption.</summary>
    public event EventHandler? ViewChanged;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ImageSurface),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty ShowCheckerboardProperty = DependencyProperty.Register(
        nameof(ShowCheckerboard), typeof(bool), typeof(ImageSurface),
        new PropertyMetadata(false, OnShowCheckerboardChanged));

    public static readonly DependencyProperty AllowUpscaleInFitProperty = DependencyProperty.Register(
        nameof(AllowUpscaleInFit), typeof(bool), typeof(ImageSurface),
        new PropertyMetadata(false));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Checkerboard behind the picture, for images that carry an alpha channel.</summary>
    public bool ShowCheckerboard
    {
        get => (bool)GetValue(ShowCheckerboardProperty);
        set => SetValue(ShowCheckerboardProperty, value);
    }

    /// <summary>Documents fit the whole viewport even when that enlarges the page.</summary>
    public bool AllowUpscaleInFit
    {
        get => (bool)GetValue(AllowUpscaleInFitProperty);
        set => SetValue(AllowUpscaleInFitProperty, value);
    }

    public double ZoomPercent => Math.Round(_scale * 100);

    public double Rotation => _angle;

    public string ModeName => _fit ? "适应窗口" : _scale == 1 ? "原始大小" : "自由缩放";

    public bool IsFitMode => _fit;

    private double DisplayWidth => ((int)_angle & 180) == 0 ? _pixelWidth : _pixelHeight;

    private double DisplayHeight => ((int)_angle & 180) == 0 ? _pixelHeight : _pixelWidth;

    // --- Public actions ----------------------------------------------------------

    /// <summary>Shows the whole picture inside the viewport, never enlarging it past 1:1
    /// unless <see cref="AllowUpscaleInFit"/> says otherwise.</summary>
    public void Fit()
    {
        _fit = true;
        Apply();
    }

    /// <summary>One image pixel per device-independent pixel, centred.</summary>
    public void ActualSize()
    {
        _fit = false;
        _scale = 1;
        _offsetX = 0;
        _offsetY = 0;
        Apply();
    }

    /// <summary>Rotates the view clockwise; the file on disk is untouched.</summary>
    public void Rotate(double degrees)
    {
        _angle = (_angle + degrees + 360) % 360;
        _offsetX = 0;
        _offsetY = 0;
        Apply();
    }

    public void ZoomAtCenter(double factor) =>
        ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), factor);

    /// <summary>Shows a decode failure inside the plane instead of an empty viewer.</summary>
    public void ShowStatus(string? text)
    {
        StatusHint.Text = text ?? string.Empty;
        StatusHint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 排查「放大图底部丢失」用的几何取证：把视口与平面的实际尺寸、X/Y 缩放、平移、
    /// 以及两者在屏幕上的物理坐标一次倒出。PointToScreen 会穿过全部渲染变换，
    /// 所以平面底边的物理坐标就是它真正被画到的位置——和截图测量直接对账。
    /// </summary>
    public void DumpGeometry(string tag)
    {
        try
        {
            if (Preview.Source is null)
            {
                AppLog.Debug($"看图几何[{tag}]：无图，视口 {Viewport.ActualWidth:0}×{Viewport.ActualHeight:0}");
                return;
            }

            var planeTop = Plane.PointToScreen(new Point(0, 0));
            var planeBottom = Plane.PointToScreen(new Point(0, Plane.ActualHeight));
            var viewTop = PointToScreen(new Point(0, 0));
            var viewBottom = PointToScreen(new Point(0, Viewport.ActualHeight));
            AppLog.Debug($"看图几何[{tag}]：视口 {Viewport.ActualWidth:0}×{Viewport.ActualHeight:0}（物理 y " +
                         $"{viewTop.Y:0}→{viewBottom.Y:0}），图片 {_pixelWidth}×{_pixelHeight}，" +
                         $"Plane {Plane.ActualWidth:0}×{Plane.ActualHeight:0}（物理 y {planeTop.Y:0}→{planeBottom.Y:0}，" +
                         $"左 x {planeTop.X:0}），缩放 {_scale:0.####}，" +
                         $"位移 ({_offsetX:0.#},{_offsetY:0.#})，" +
                         $"Margin ({Plane.Margin.Left:0.#},{Plane.Margin.Top:0.#})，模式 {ModeName}");
        }
        catch (Exception ex)
        {
            AppLog.Debug($"看图几何[{tag}]：失败 {ex.Message}");
        }
    }

    // --- Layout ------------------------------------------------------------------

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (ImageSurface)d;
        surface.AdoptSource((ImageSource?)e.NewValue);
    }

    private static void OnShowCheckerboardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (ImageSurface)d;
        surface.CheckerViewport();
    }

    private void CheckerViewport()
    {
        Checker.Visibility = ShowCheckerboard && Source is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AdoptSource(ImageSource? source)
    {
        _source = source;
        _rotated = null;
        _rotatedAngle = -1;

        if (source is BitmapSource bitmap)
        {
            _pixelWidth = bitmap.PixelWidth;
            _pixelHeight = bitmap.PixelHeight;
        }
        else if (source is not null)
        {
            _pixelWidth = Math.Max(1, (int)source.Width);
            _pixelHeight = Math.Max(1, (int)source.Height);
        }
        else
        {
            _pixelWidth = _pixelHeight = 0;
        }

        _angle = 0;
        _fit = true;
        _offsetX = _offsetY = 0;
        _scale = 1;
        CheckerViewport();
        Apply();
        string bottomSample = source is BitmapSource bound ? Services.BitmapDiagnostics.DescribeBottomRows(bound) : "非位图";
        AppLog.Debug($"看图平面装载：图片 {_pixelWidth}×{_pixelHeight}，{bottomSample}，视口 " +
                     $"{Viewport.ActualWidth:0}×{Viewport.ActualHeight:0}，缩放 {_scale:0.###}，模式 {ModeName}");
    }

    private void Apply()
    {
        double viewWidth = Viewport.ActualWidth;
        double viewHeight = Viewport.ActualHeight;
        if (_pixelWidth <= 0 || _pixelHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return;

        if (_fit)
        {
            double fit = Math.Min(viewWidth / DisplayWidth, viewHeight / DisplayHeight);
            if (!AllowUpscaleInFit) fit = Math.Min(1, fit);
            _scale = Math.Max(fit, MinZoom);
            _offsetX = 0;
            _offsetY = 0;
        }

        ClampPan(viewWidth, viewHeight);

        // 缩放/平移/旋转全部落在平面自身的布局尺寸与 Margin 上：平面按「旋转后的位图
        // 尺寸 × 缩放」布局，Image 拉伸铺满。不用 RenderTransform——部分机器的 WPF 会把
        // 「比视口高的平面 + RenderTransform 缩放」的底部整块裁掉（探针复现：1718×1204
        // 缩放 0.8412 后底部 161px 不绘制；改为布局尺寸后完整）。
        Plane.Width = DisplayWidth * _scale;
        Plane.Height = DisplayHeight * _scale;
        Plane.Margin = new Thickness(
            viewWidth / 2 - Plane.Width / 2 + _offsetX,
            viewHeight / 2 - Plane.Height / 2 + _offsetY, 0, 0);

        ImageSource? shown = RotatedSource();
        if (!ReferenceEquals(Preview.Source, shown)) Preview.Source = shown;

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>原始图像源（未旋转），角度变化时按需包一层 TransformedBitmap。</summary>
    private ImageSource? _source;
    private ImageSource? _rotated;
    private int _rotatedAngle = -1;

    /// <summary>
    /// 按当前视角角度给出图像源：0° 直接用原图，90/180/270° 用 TransformedBitmap 预旋转。
    /// 平面随后按旋转后的宽高布局，整个链路不产生 RenderTransform。
    /// </summary>
    private ImageSource? RotatedSource()
    {
        if (_source is null) return null;
        int quarter = ((int)_angle % 360 + 360) % 360;
        if (quarter == 0) return _source;
        if (quarter == _rotatedAngle && _rotated is not null) return _rotated;
        if (_source is not BitmapSource bitmap) return _source;

        var turned = new TransformedBitmap(bitmap, quarter switch
        {
            90 => new RotateTransform(90),
            180 => new RotateTransform(180),
            _ => new RotateTransform(270),
        });
        turned.Freeze();
        _rotated = turned;
        _rotatedAngle = quarter;
        return turned;
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

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Apply();
        AppLog.Debug($"看图平面视口变化：{Viewport.ActualWidth:0}×{Viewport.ActualHeight:0}，" +
                     $"图片 {_pixelWidth}×{_pixelHeight}，缩放 {_scale:0.###}，模式 {ModeName}，" +
                     $"位移 ({_offsetX:0},{_offsetY:0})");
    }

    // --- Zoom & pan --------------------------------------------------------------

    private void ZoomAt(Point cursor, double factor)
    {
        if (_pixelWidth <= 0) return;

        double target = Math.Clamp(_scale * factor, MinZoom, MaxZoom);
        double change = target / _scale;
        if (Math.Abs(change - 1) < 0.001) return;

        // Keep the picture pixel under the cursor pinned while the scale changes.
        double centerX = Viewport.ActualWidth / 2 + _offsetX;
        double centerY = Viewport.ActualHeight / 2 + _offsetY;
        _offsetX += (cursor.X - centerX) * (1 - change);
        _offsetY += (cursor.Y - centerY) * (1 - change);
        _scale = target;
        _fit = false;
        Apply();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_pixelWidth <= 0) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1.06 : 1.15;
        ZoomAt(e.GetPosition(Viewport), e.Delta > 0 ? step : 1 / step);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_pixelWidth <= 0 || e.OriginalSource is System.Windows.Controls.Primitives.ScrollBar) return;

        if (e.ClickCount >= 2)
        {
            if (_fit) ActualSize();
            else Fit();
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panFromX = _offsetX;
        _panFromY = _offsetY;
        CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_panning) return;

        Point position = e.GetPosition(Viewport);
        _offsetX = _panFromX + (position.X - _panStart.X);
        _offsetY = _panFromY + (position.Y - _panStart.Y);
        _fit = false;
        Apply();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_panning) return;
        EndPan();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_panning && e.LeftButton == MouseButtonState.Released) EndPan();
    }

    private void EndPan()
    {
        _panning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }
}
