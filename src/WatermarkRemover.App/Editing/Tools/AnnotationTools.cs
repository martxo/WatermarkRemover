using System.Windows;
using System.Windows.Input;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App.Editing;

/// <summary>五个工具的共享构造助手：把样式状态 + 缩放比烧进不可变元素。</summary>
internal static class AnnotationToolFactory
{
    public static ScreenshotAnnotation Span(
        in AnnotationToolContext context, ScreenshotAnnotationKind kind, Point start, Point end)
    {
        float thickness = Math.Max(context.StrokeFloor, context.Style.LineWidthDip * context.Scale);
        return new ScreenshotAnnotation
        {
            Kind = kind,
            Points = new[]
            {
                new ScreenshotPointF((float)start.X, (float)start.Y),
                new ScreenshotPointF((float)end.X, (float)end.Y),
            },
            ColorArgb = context.Style.ColorArgb,
            Thickness = Math.Max(1, thickness),
        };
    }

    /// <summary>推进中的草稿保留原有的颜色/粗细（拖拽中途改样式不影响已起步的这一笔）。</summary>
    public static ScreenshotAnnotation WithPoints(
        ScreenshotAnnotation draft, IReadOnlyList<ScreenshotPointF> points) => new()
        {
            Kind = draft.Kind,
            Points = points,
            ColorArgb = draft.ColorArgb,
            Thickness = draft.Thickness,
            Text = draft.Text,
            FontSize = draft.FontSize,
        };

    public static bool SpansEnough(in AnnotationToolContext context, ScreenshotAnnotation draft)
    {
        if (draft.Points.Count < 2) return false;
        float width = Math.Abs(draft.Points[^1].X - draft.Points[0].X);
        float height = Math.Abs(draft.Points[^1].Y - draft.Points[0].Y);
        return context.RequireBothSides
            ? width >= context.MinMarkSpan && height >= context.MinMarkSpan
            : width >= context.MinMarkSpan || height >= context.MinMarkSpan;
    }
}

/// <summary>矩形：两点拖拽。</summary>
internal sealed class RectangleTool : IAnnotationTool
{
    public ScreenshotTool Tool => ScreenshotTool.Rectangle;

    public ScreenshotAnnotationKind Kind => ScreenshotAnnotationKind.Rectangle;

    public string DisplayName => "矩形";

    public string Description => "拖拽画矩形框";

    public Key Shortcut => Key.R;

    public bool NeedsDrag => true;

    public ScreenshotAnnotation Begin(in AnnotationToolContext context, Point start) =>
        AnnotationToolFactory.Span(context, Kind, start, start);

    public ScreenshotAnnotation Update(in AnnotationToolContext context, ScreenshotAnnotation draft, Point end) =>
        AnnotationToolFactory.Span(context, Kind, new Point(draft.Points[0].X, draft.Points[0].Y), end);

    public bool Keep(in AnnotationToolContext context, ScreenshotAnnotation draft) =>
        AnnotationToolFactory.SpansEnough(context, draft);
}

/// <summary>箭头：直线 + 三角箭头（头部由合成器绘制）。</summary>
internal sealed class ArrowTool : IAnnotationTool
{
    public ScreenshotTool Tool => ScreenshotTool.Arrow;

    public ScreenshotAnnotationKind Kind => ScreenshotAnnotationKind.Arrow;

    public string DisplayName => "箭头";

    public string Description => "拖拽画箭头";

    public Key Shortcut => Key.A;

    public bool NeedsDrag => true;

    public ScreenshotAnnotation Begin(in AnnotationToolContext context, Point start) =>
        AnnotationToolFactory.Span(context, Kind, start, start);

    public ScreenshotAnnotation Update(in AnnotationToolContext context, ScreenshotAnnotation draft, Point end) =>
        AnnotationToolFactory.Span(context, Kind, new Point(draft.Points[0].X, draft.Points[0].Y), end);

    public bool Keep(in AnnotationToolContext context, ScreenshotAnnotation draft) =>
        AnnotationToolFactory.SpansEnough(context, draft);
}

/// <summary>画笔：自由路径，按最小间距采点。</summary>
internal sealed class PenTool : IAnnotationTool
{
    public ScreenshotTool Tool => ScreenshotTool.Pen;

    public ScreenshotAnnotationKind Kind => ScreenshotAnnotationKind.Pen;

    public string DisplayName => "画笔";

    public string Description => "按住拖动自由绘制";

    public Key Shortcut => Key.P;

    public bool NeedsDrag => true;

    public ScreenshotAnnotation Begin(in AnnotationToolContext context, Point start)
    {
        // 起笔只有一个采点；Span 负责颜色/粗细，WithPoints 把点数收敛为 1。
        ScreenshotAnnotation seed = AnnotationToolFactory.Span(context, Kind, start, start);
        return AnnotationToolFactory.WithPoints(
            seed, new[] { new ScreenshotPointF((float)start.X, (float)start.Y) });
    }

    public ScreenshotAnnotation Update(in AnnotationToolContext context, ScreenshotAnnotation draft, Point end)
    {
        var last = draft.Points[^1];
        if (Math.Abs(last.X - end.X) < context.MinPenStep && Math.Abs(last.Y - end.Y) < context.MinPenStep)
            return draft;

        var points = new List<ScreenshotPointF>(draft.Points)
        {
            new((float)end.X, (float)end.Y),
        };
        return AnnotationToolFactory.WithPoints(draft, points);
    }

    public bool Keep(in AnnotationToolContext context, ScreenshotAnnotation draft) =>
        draft.Points.Count >= 2;
}

/// <summary>文字：点击放置锚点，由宿主弹出浮层输入（不产生拖拽草稿）。</summary>
internal sealed class TextTool : IAnnotationTool
{
    public ScreenshotTool Tool => ScreenshotTool.Text;

    public ScreenshotAnnotationKind Kind => ScreenshotAnnotationKind.Text;

    public string DisplayName => "文字";

    public string Description => "点击后输入文字，回车确认";

    public Key Shortcut => Key.T;

    public bool NeedsDrag => false;

    // 文字走锚点 + 浮层，下列成员不会被会话调用（NeedsDrag=false 时 Begin 只置锚点）。
    public ScreenshotAnnotation Begin(in AnnotationToolContext context, Point start) =>
        AnnotationToolFactory.Span(context, Kind, start, start);

    public ScreenshotAnnotation Update(in AnnotationToolContext context, ScreenshotAnnotation draft, Point end) => draft;

    public bool Keep(in AnnotationToolContext context, ScreenshotAnnotation draft) => false;
}

/// <summary>马赛克：矩形区域像素化（合成器按 12px 块处理）。</summary>
internal sealed class MosaicTool : IAnnotationTool
{
    public ScreenshotTool Tool => ScreenshotTool.Mosaic;

    public ScreenshotAnnotationKind Kind => ScreenshotAnnotationKind.Mosaic;

    public string DisplayName => "马赛克";

    public string Description => "拖拽涂抹遮挡敏感内容";

    public Key Shortcut => Key.M;

    public bool NeedsDrag => true;

    public ScreenshotAnnotation Begin(in AnnotationToolContext context, Point start) =>
        AnnotationToolFactory.Span(context, Kind, start, start);

    public ScreenshotAnnotation Update(in AnnotationToolContext context, ScreenshotAnnotation draft, Point end) =>
        AnnotationToolFactory.Span(context, Kind, new Point(draft.Points[0].X, draft.Points[0].Y), end);

    public bool Keep(in AnnotationToolContext context, ScreenshotAnnotation draft) =>
        AnnotationToolFactory.SpansEnough(context, draft);
}
