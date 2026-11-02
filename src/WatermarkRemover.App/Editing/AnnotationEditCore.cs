using System.Windows;
using System.Windows.Input;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App.Editing;

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

/// <summary>
/// 工具栏选中的颜色 / 粗细 / 字号。取值一律是「设备无关像素（DIP）」，由宿主注入的缩放比
/// 换算成画布物理像素——截图遮罩按指针所在显示器的 scale，标注窗按显示缩放的倒数。
/// </summary>
public sealed class AnnotationStyleState
{
    private uint _colorArgb = ScreenshotAnnotation.DefaultColorArgb;
    private float _lineWidthDip = 2.5f;
    private float _textFontSizeDip = 16f;

    public uint ColorArgb => _colorArgb;

    /// <summary>Stroke width in device-independent pixels.</summary>
    public float LineWidthDip => _lineWidthDip;

    /// <summary>Text annotation font size in device-independent pixels (12 / 16 / 20).</summary>
    public float TextFontSizeDip => _textFontSizeDip;

    internal bool SetColor(uint argb)
    {
        if (_colorArgb == argb) return false;
        _colorArgb = argb;
        return true;
    }

    internal bool SetLineWidth(float dip)
    {
        if (Math.Abs(_lineWidthDip - dip) < 0.01f) return false;
        _lineWidthDip = dip;
        return true;
    }

    internal bool SetTextFontSize(float dip)
    {
        if (Math.Abs(_textFontSizeDip - dip) < 0.01f) return false;
        _textFontSizeDip = dip;
        return true;
    }
}

/// <summary>
/// 一个标注工具的交互逻辑。新增工具 = 写一个实现并注册进 <see cref="AnnotationToolRegistry"/>：
/// 工具条按钮、快捷键、草稿状态机、撤销/重做、导出与 PDF 侧全部自动接入，宿主零改动。
/// </summary>
public interface IAnnotationTool
{
    ScreenshotTool Tool { get; }

    ScreenshotAnnotationKind Kind { get; }

    /// <summary>工具条按钮上的文字。</summary>
    string DisplayName { get; }

    /// <summary>工具条按钮的提示文案。</summary>
    string Description { get; }

    /// <summary>单键快捷键（R/A/P/T/M…）。</summary>
    Key Shortcut { get; }

    /// <summary>false = 点击放置型工具（文字），不产生拖拽草稿。</summary>
    bool NeedsDrag { get; }

    /// <summary>指针按下：生成初始草稿。</summary>
    ScreenshotAnnotation Begin(in AnnotationToolContext context, Point start);

    /// <summary>指针移动：推进草稿（画笔追加采点，其余重设末点）。</summary>
    ScreenshotAnnotation Update(in AnnotationToolContext context, ScreenshotAnnotation draft, Point end);

    /// <summary>指针抬起：这个草稿值得保留吗（最小尺寸过滤）。</summary>
    bool Keep(in AnnotationToolContext context, ScreenshotAnnotation draft);
}

/// <summary>
/// 工具执行时所需的换算与策略，由 <see cref="AnnotationEditSession"/> 在每次指针事件时构造。
/// </summary>
public readonly record struct AnnotationToolContext(
    AnnotationStyleState Style,
    /// <summary>每个 DIP 对应的画布单位数（截图 = 显示器 scale，标注窗 = 1 / 显示缩放）。</summary>
    float Scale,
    /// <summary>笔画物理像素下限（截图 2，标注窗 1）。</summary>
    float StrokeFloor,
    /// <summary>非画笔草稿提交的最小跨度（截图 4，标注窗 3）。</summary>
    float MinMarkSpan,
    /// <summary>画笔采点最小间距（截图 1.5，标注窗 1）。</summary>
    float MinPenStep,
    /// <summary>true = 宽高都需达标才保留（截图现状）；false = 任一边达标即可（标注窗现状）。</summary>
    bool RequireBothSides);

/// <summary>
/// 工具注册表：「注册即接入」的落点。默认注册现有五个工具；新工具只需
/// <see cref="Register"/> 一个 <see cref="IAnnotationTool"/> 实现。
/// </summary>
public sealed class AnnotationToolRegistry
{
    private readonly Dictionary<ScreenshotTool, IAnnotationTool> _byTool = new();
    private readonly Dictionary<Key, IAnnotationTool> _byKey = new();

    /// <summary>进程级默认注册表（五个现有工具）。</summary>
    public static AnnotationToolRegistry Default { get; } = CreateDefault();

    public IReadOnlyCollection<IAnnotationTool> Tools => _byTool.Values;

    public IAnnotationTool Register(IAnnotationTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _byTool[tool.Tool] = tool;
        _byKey[tool.Shortcut] = tool;
        return tool;
    }

    public IAnnotationTool? Find(ScreenshotTool tool) => _byTool.GetValueOrDefault(tool);

    public IAnnotationTool? FindByKey(Key key) => _byKey.GetValueOrDefault(key);

    private static AnnotationToolRegistry CreateDefault()
    {
        var registry = new AnnotationToolRegistry();
        registry.Register(new RectangleTool());
        registry.Register(new ArrowTool());
        registry.Register(new PenTool());
        registry.Register(new TextTool());
        registry.Register(new MosaicTool());
        return registry;
    }
}

/// <summary>
/// 标注历史的撤销 / 重做栈（快照式：元素不可变，栈里放元素引用即可）。
/// 收敛截图遮罩与标注窗此前的两份 List+List 重复实现。
/// </summary>
public sealed class AnnotationHistory
{
    private readonly List<ScreenshotAnnotation> _applied = new();
    private readonly List<ScreenshotAnnotation> _undone = new();

    public int Count => _applied.Count;

    public bool CanUndo => _applied.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public IReadOnlyList<ScreenshotAnnotation> Items => _applied;

    public event EventHandler? Changed;

    public void Add(ScreenshotAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        _applied.Add(annotation);
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_applied.Count == 0) return false;
        _undone.Add(_applied[^1]);
        _applied.RemoveAt(_applied.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_undone.Count == 0) return false;
        _applied.Add(_undone[^1]);
        _undone.RemoveAt(_undone.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Clear()
    {
        if (_applied.Count == 0 && _undone.Count == 0) return false;
        _applied.Clear();
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>
/// 与数据源无关的编辑会话：工具选择 + 样式状态 + 撤销栈 + 草稿状态机 + 文字锚点。
/// 截图遮罩、标注窗、PDF 页面编辑三个宿主共用；宿主只负责进出口坐标换算与界面呈现。
/// </summary>
public sealed class AnnotationEditSession
{
    private readonly AnnotationStyleState _style = new();
    private readonly AnnotationHistory _history = new();
    private IAnnotationTool? _tool;
    private ScreenshotAnnotation? _draft;
    private Point? _textAnchor;

    public AnnotationEditSession()
    {
        _history.Changed += (_, _) =>
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    // --- 宿主注入的换算与策略 -------------------------------------------------

    /// <summary>画布坐标 → 每个 DIP 对应的画布单位数。默认 1（画布即 DIP 空间）。</summary>
    public Func<Point, float> ScaleAt { get; set; } = _ => 1f;

    /// <summary>提交时把画布坐标换算成标注坐标（截图 = 减去选区原点）；null = 恒等。</summary>
    public Func<Point, Point>? ToLocal { get; set; }

    /// <summary>文字锚点是否允许落点（截图 = 锚点必须在选区内）；null = 不校验。</summary>
    public Func<Point, bool>? CanPlaceText { get; set; }

    /// <summary>笔画物理像素下限。</summary>
    public float StrokeFloor { get; set; } = 1f;

    /// <summary>非画笔草稿提交的最小跨度。</summary>
    public float MinMarkSpan { get; set; } = 3f;

    /// <summary>画笔采点最小间距。</summary>
    public float MinPenStep { get; set; } = 1f;

    /// <summary>true = 宽高都需达标才保留；false = 任一边达标即可。</summary>
    public bool RequireBothSides { get; set; }

    // --- 状态 -----------------------------------------------------------------

    public AnnotationStyleState Style => _style;

    public AnnotationHistory History => _history;

    public ScreenshotTool Tool => _tool?.Tool ?? ScreenshotTool.None;

    /// <summary>拖拽中的草稿（画布坐标），由宿主渲染实时预览形状。</summary>
    public ScreenshotAnnotation? Draft => _draft;

    /// <summary>文字工具的落点锚（画布坐标），宿主据此弹出文字浮层。</summary>
    public Point? TextAnchor => _textAnchor;

    public bool HasAnnotations => _history.Count > 0;

    public bool CanRedo => _history.CanRedo;

    public IReadOnlyList<ScreenshotAnnotation> Annotations => _history.Items;

    // --- 事件（宿主按需订阅，避免多余的重算） -----------------------------------

    /// <summary>任何状态变化（便捷总线）。</summary>
    public event EventHandler? Changed;

    /// <summary>标注集合变化：需要重算「已确认标注」的预览。</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>草稿变化：需要重画草稿形状。</summary>
    public event EventHandler? DraftChanged;

    /// <summary>工具/颜色/粗细/字号变化：需要刷新工具条状态。</summary>
    public event EventHandler? StyleChanged;

    /// <summary>文字锚点出现/消失：需要弹出/收起文字浮层。</summary>
    public event EventHandler? TextAnchorChanged;

    // --- 工具与样式 -------------------------------------------------------------

    /// <summary>选择工具（再点一次当前工具 = 取消选择），同时放弃未提交的文字锚点。</summary>
    public void SelectTool(ScreenshotTool tool)
    {
        _tool = tool == ScreenshotTool.None || _tool?.Tool == tool
            ? null
            : AnnotationToolRegistry.Default.Find(tool);

        bool anchorWasSet = ClearTextAnchor();
        if (anchorWasSet) TextAnchorChanged?.Invoke(this, EventArgs.Empty);
        StyleChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetColor(uint argb)
    {
        if (_style.SetColor(argb)) RaiseStyle();
    }

    public void SetLineWidth(float dip)
    {
        if (_style.SetLineWidth(dip)) RaiseStyle();
    }

    public void SetTextFontSize(float dip)
    {
        if (_style.SetTextFontSize(dip)) RaiseStyle();
    }

    // --- 指针 → 草稿 -------------------------------------------------------------

    /// <summary>指针按下。文字工具置锚点，其余生成初始草稿。</summary>
    public bool Begin(Point point)
    {
        if (_tool is not { } tool) return false;

        if (!tool.NeedsDrag)
        {
            if (CanPlaceText is { } check && !check(point)) return false;
            _textAnchor = point;
            TextAnchorChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        _draft = tool.Begin(ContextAt(point), point);
        DraftChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>指针移动：推进草稿。</summary>
    public bool Update(Point point)
    {
        if (_draft is not { } draft || _tool is not { } tool) return false;

        ScreenshotAnnotation updated = tool.Update(ContextAt(point), draft, point);
        if (ReferenceEquals(updated, draft)) return true;

        _draft = updated;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>指针抬起：过滤后提交草稿（含画布坐标 → 标注坐标的换算）。</summary>
    public bool Commit()
    {
        if (_draft is not { } draft || _tool is not { } tool)
        {
            if (_draft is null) return false;
            _draft = null;
            DraftChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _draft = null;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);

        if (!tool.Keep(ContextAt(default), draft)) return false;

        _history.Add(Translate(draft));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // --- 文字 -------------------------------------------------------------------

    /// <summary>提交文字浮层的内容；空白文本 = 放弃。</summary>
    public void CommitText(string? text)
    {
        if (_textAnchor is not { } anchor) return;

        bool anchorWasSet = ClearTextAnchor();
        bool added = false;
        if (!string.IsNullOrWhiteSpace(text) && (CanPlaceText is not { } check || check(anchor)))
        {
            Point local = ToLocal is { } convert ? convert(anchor) : anchor;
            _history.Add(new ScreenshotAnnotation
            {
                Kind = ScreenshotAnnotationKind.Text,
                Points = new[] { new ScreenshotPointF((float)local.X, (float)local.Y) },
                Text = text.Trim(),
                FontSize = Math.Max(10, _style.TextFontSizeDip * ScaleAt(anchor)),
                ColorArgb = _style.ColorArgb,
                Thickness = 1f,
            });
            added = true;
        }

        if (anchorWasSet) TextAnchorChanged?.Invoke(this, EventArgs.Empty);
        if (!added) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>放弃文字输入（Esc）。</summary>
    public void CancelText()
    {
        if (ClearTextAnchor())
        {
            TextAnchorChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // --- 历史 ---------------------------------------------------------------------

    public bool Undo()
    {
        return _history.Undo();
    }

    public bool Redo()
    {
        return _history.Redo();
    }

    /// <summary>清空全部标注（工具栏「清空」与截图重选选区时共用）。</summary>
    public bool Clear()
    {
        return _history.Clear();
    }

    // --- 内部 -----------------------------------------------------------------------

    private void RaiseStyle()
    {
        StyleChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool ClearTextAnchor()
    {
        if (_textAnchor is null) return false;
        _textAnchor = null;
        return true;
    }

    private AnnotationToolContext ContextAt(Point point) => new(
        _style, ScaleAt(point), StrokeFloor, MinMarkSpan, MinPenStep, RequireBothSides);

    /// <summary>按宿主的坐标约定把草稿的画布坐标换算成标注坐标。</summary>
    private ScreenshotAnnotation Translate(ScreenshotAnnotation draft)
    {
        if (ToLocal is not { } convert) return draft;

        var points = new ScreenshotPointF[draft.Points.Count];
        for (int i = 0; i < points.Length; i++)
        {
            Point local = convert(new Point(draft.Points[i].X, draft.Points[i].Y));
            points[i] = new ScreenshotPointF((float)local.X, (float)local.Y);
        }

        return new ScreenshotAnnotation
        {
            Kind = draft.Kind,
            Points = points,
            ColorArgb = draft.ColorArgb,
            Thickness = draft.Thickness,
            Text = draft.Text,
            FontSize = draft.FontSize,
        };
    }
}
