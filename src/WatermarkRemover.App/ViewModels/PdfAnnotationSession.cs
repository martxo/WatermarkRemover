using WatermarkRemover.App.Editing;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// 按页标注会话：每页一份 <see cref="AnnotationEditSession"/>（各自的撤销栈），翻页不丢、
/// 脏页可精确回写；工具/颜色/粗细/字号跨页继承——翻页不会把选好的红色粗笔重置回默认。
/// 编辑逻辑全部在内核里，这里只做「页 ↔ 会话」的映射与脏页登记，不含任何按工具的分支。
/// </summary>
public sealed class PdfAnnotationSession
{
    private readonly Dictionary<int, AnnotationEditSession> _sessions = new();
    private uint _color = ScreenshotAnnotation.DefaultColorArgb;
    private float _lineWidth = 2.5f;
    private float _fontSize = 16f;
    private int _page;

    /// <summary>任何一页的标注集合变化（宿主刷新预览 / 脏状态）。</summary>
    public event EventHandler? Changed;

    /// <summary>任何一页的样式变化（宿主刷工具条与指针抢占状态）。</summary>
    public event EventHandler? StyleChanged;

    /// <summary>当前页（<see cref="SwitchTo"/> 设置）；-1 = 尚未加载。</summary>
    public int CurrentPage => _page;

    /// <summary>有标注的页（脏页），升序——保存时只碰这些页。</summary>
    public IReadOnlyList<int> DirtyPages =>
        _sessions.Where(pair => pair.Value.HasAnnotations).Select(pair => pair.Key).OrderBy(i => i).ToList();

    public bool HasAnnotations => _sessions.Values.Any(session => session.HasAnnotations);

    /// <summary>某页已确认的标注；没有则空。</summary>
    public IReadOnlyList<ScreenshotAnnotation> AnnotationsOf(int pageIndex) =>
        _sessions.TryGetValue(pageIndex, out AnnotationEditSession? session)
            ? session.Annotations
            : Array.Empty<ScreenshotAnnotation>();

    /// <summary>
    /// 当前页的内核会话（工具条与覆盖层直接驱动它）。按需创建：新会话继承上次使用的样式，
    /// 所以换页不丢选择；历史变化与样式变化分别向宿主广播。
    /// </summary>
    public AnnotationEditSession Current => SessionFor(_page);

    public AnnotationEditSession SessionFor(int pageIndex)
    {
        if (_sessions.TryGetValue(pageIndex, out AnnotationEditSession? existing)) return existing;

        var session = new AnnotationEditSession
        {
            StrokeFloor = 1f,
            MinMarkSpan = 3f,
            MinPenStep = 1f,
            // 与截图标注窗一致（任一边达标即留）；截图遮罩侧的语义用户决策中，不牵连这里。
            RequireBothSides = false,
        };
        session.SetColor(_color);
        session.SetLineWidth(_lineWidth);
        session.SetTextFontSize(_fontSize);
        session.StyleChanged += (_, _) =>
        {
            _color = session.Style.ColorArgb;
            _lineWidth = session.Style.LineWidthDip;
            _fontSize = session.Style.TextFontSizeDip;
            StyleChanged?.Invoke(this, EventArgs.Empty);
        };
        session.HistoryChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        _sessions[pageIndex] = session;
        return session;
    }

    /// <summary>翻页：记住新当前页（会话按需创建，旧页的标注原样留在字典里）。</summary>
    public void SwitchTo(int pageIndex)
    {
        if (pageIndex == _page) return;
        _page = pageIndex;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清掉某页的内存标注——保存落库后调用：磁盘上已有这些标注，重渲染后的页面就是新底图，
    /// 继续标注会在新底图上叠加，不会把同一批标注画两遍。
    /// </summary>
    public void ClearPage(int pageIndex)
    {
        if (!_sessions.TryGetValue(pageIndex, out AnnotationEditSession? session)) return;
        if (!session.HasAnnotations) return;

        session.Clear();
    }

    /// <summary>退出标注：全部会话作废（磁盘文件从未被内存标注污染）。</summary>
    public void Reset()
    {
        foreach (AnnotationEditSession session in _sessions.Values)
            session.Clear();
        _sessions.Clear();
    }
}
