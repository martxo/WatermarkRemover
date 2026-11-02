using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WatermarkRemover.App.Editing;

namespace WatermarkRemover.App.Controls;

/// <summary>
/// 标注覆盖层：指针捕获 + 草稿 Shape + 文字浮层，叠在图片平面（ImageSurface 的覆盖层
/// 挂载点）之上。编辑逻辑全部委托 <see cref="AnnotationEditSession"/>——本控件只做
/// 「宿主坐标换算 → 会话 → 画草稿 / 弹浮层」，不含任何按工具的分支；新工具注册进
/// <see cref="AnnotationToolRegistry"/> 后自动可用。截图查看器（阶段 1）与 PDF 页预览
/// （阶段 2）共用同一控件，换宿主只换注入的坐标换算。
/// </summary>
public partial class AnnotationOverlay : UserControl
{
    private AnnotationEditSession? _session;
    private bool _drawing;

    public AnnotationOverlay()
    {
        InitializeComponent();
        Editor.Completed += (_, text) =>
        {
            if (_session is null) return;
            if (text is null) _session.CancelText();
            else _session.CommitText(text);
        };
    }

    /// <summary>编辑会话（宿主注入；换图片/换页时换成新会话）。</summary>
    public AnnotationEditSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.DraftChanged -= OnDraftChanged;
                _session.TextAnchorChanged -= OnTextAnchorChanged;
            }

            _session = value;
            if (_session is not null)
            {
                _session.DraftChanged += OnDraftChanged;
                _session.TextAnchorChanged += OnTextAnchorChanged;
            }

            DrawDraft();
        }
    }

    /// <summary>平面坐标（DIP）→ 画布坐标（宿主数据源单位，通常是原图像素）。</summary>
    public Func<Point, Point>? ToCanvas { get; set; }

    /// <summary>画布坐标 → 平面坐标（DIP）；文字浮层定位用。</summary>
    public Func<Point, Point>? ToPlane { get; set; }

    /// <summary>按当前工具刷新指针形状（宿主在样式变化时调用）。</summary>
    public void UpdateCursor()
    {
        DraftLayer.Cursor = _session?.Tool switch
        {
            ScreenshotTool.Text => Cursors.IBeam,
            null or ScreenshotTool.None => Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    private Point Canvas(Point plane) => ToCanvas is { } convert ? convert(plane) : plane;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;

        if (Editor.IsActive)
        {
            // 画布上点击 = 提交浮层文字（与标注窗语义一致）。
            Editor.Submit();
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            // 双击让给宿主（看图器 = 适应/100% 切换），不开始绘制。
            return;
        }

        if (_session.Tool == ScreenshotTool.None) return;

        Point point = Canvas(e.GetPosition(DraftLayer));
        if (!_session.Begin(point)) return;

        // 是否需要拖拽由工具自己声明（文字 = 点击放置），覆盖层不按 Kind 分支。
        if (AnnotationToolRegistry.Default.Find(_session.Tool) is { NeedsDrag: true })
        {
            _drawing = true;
            DraftLayer.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _session is null) return;
        _session.Update(Canvas(e.GetPosition(DraftLayer)));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;

        _drawing = false;
        DraftLayer.ReleaseMouseCapture();
        _session?.Commit();
        e.Handled = true;
    }

    private void OnDraftChanged(object? sender, EventArgs e) => DrawDraft();

    private void DrawDraft()
    {
        if (_session?.Draft is not { } draft)
        {
            DraftLayer.Children.Clear();
            return;
        }

        // 实时预览用平面坐标（DIP），粗细按 DIP；几何与最终合成器保持一致。
        Func<Point, Point> toPlane = ToPlane ?? (point => point);
        DraftShapeRenderer.Render(DraftLayer, draft, toPlane, new DraftShapeRenderer.Options(
            DraftShapeRenderer.MarkColor(_session.Style.ColorArgb),
            _session.Style.LineWidthDip,
            Color.FromArgb(24, 0, 0, 0),
            Color.FromRgb(160, 160, 160),
            new DoubleCollection { 3, 2 }));
    }

    private void OnTextAnchorChanged(object? sender, EventArgs e)
    {
        if (_session is null) return;

        if (_session.TextAnchor is not { } anchor)
        {
            if (Editor.IsActive) Editor.Hide();
            return;
        }

        Point display = ToPlane is { } convert ? convert(anchor) : anchor;
        Editor.Start(display, _session.Style.TextFontSizeDip,
            DraftShapeRenderer.MarkColor(_session.Style.ColorArgb), Editor.Width, Editor.Height);
    }
}
