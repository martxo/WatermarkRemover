using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using WatermarkRemover.App.Editing;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 编辑内核是截图遮罩、标注窗与后续 PDF 编辑共用的状态机：历史栈、草稿过滤、
/// 坐标换算与文字锚点全部在这里收敛，行为以这两处宿主的原有语义为准。
/// </summary>
public sealed class AnnotationEditCoreTests
{
    private static AnnotationEditSession NewSession() => new();

    private static AnnotationEditSession DrawDrag(
        AnnotationEditSession session, ScreenshotTool tool, Point start, Point end)
    {
        session.SelectTool(tool);
        session.Begin(start);
        session.Update(end);
        session.Commit();
        return session;
    }

    [Fact]
    public void Registry_exposes_the_five_tools_with_their_shortcuts()
    {
        AnnotationToolRegistry registry = AnnotationToolRegistry.Default;

        Assert.Equal(5, registry.Tools.Count);
        Assert.Equal(ScreenshotTool.Rectangle, registry.FindByKey(Key.R)?.Tool);
        Assert.Equal(ScreenshotTool.Arrow, registry.FindByKey(Key.A)?.Tool);
        Assert.Equal(ScreenshotTool.Pen, registry.FindByKey(Key.P)?.Tool);
        Assert.Equal(ScreenshotTool.Text, registry.FindByKey(Key.T)?.Tool);
        Assert.Equal(ScreenshotTool.Mosaic, registry.FindByKey(Key.M)?.Tool);
        Assert.NotNull(registry.Find(ScreenshotTool.Mosaic));
        Assert.Null(registry.Find(ScreenshotTool.None));
    }

    [Fact]
    public void Undo_redo_roundtrip_and_a_new_mark_clears_the_redo_branch()
    {
        AnnotationEditSession session = NewSession();
        DrawDrag(session, ScreenshotTool.Rectangle, new Point(0, 0), new Point(20, 20));

        Assert.True(session.Undo());
        Assert.False(session.HasAnnotations);
        Assert.True(session.CanRedo);

        Assert.True(session.Redo());
        Assert.True(session.HasAnnotations);
        Assert.False(session.CanRedo);

        // 重做之后新画一笔，重做分支必须失效（标准编辑器行为）。
        session.Undo();
        DrawDrag(session, ScreenshotTool.Arrow, new Point(0, 0), new Point(30, 30));
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void Clear_empties_annotations_and_redo()
    {
        AnnotationEditSession session = NewSession();
        DrawDrag(session, ScreenshotTool.Rectangle, new Point(0, 0), new Point(20, 20));
        session.Undo();

        Assert.True(session.Clear());
        Assert.False(session.HasAnnotations);
        Assert.False(session.CanRedo);
        // 空历史上清空是no-op，不产生事件。
        Assert.False(session.Clear());
    }

    [Fact]
    public void Drag_shorter_than_the_minimum_span_is_discarded()
    {
        // 截图遮罩语义：宽高都需要达标（RequireBothSides = true）。
        AnnotationEditSession session = new() { MinMarkSpan = 4f, RequireBothSides = true };
        DrawDrag(session, ScreenshotTool.Rectangle, new Point(0, 0), new Point(2, 40));

        Assert.False(session.HasAnnotations);
    }

    [Fact]
    public void Either_side_long_enough_keeps_the_mark()
    {
        // 标注窗语义：任一边达标即可（RequireBothSides = false），水平/垂直标记不被丢。
        AnnotationEditSession session = new() { MinMarkSpan = 3f, RequireBothSides = false };
        DrawDrag(session, ScreenshotTool.Arrow, new Point(0, 0), new Point(4, 0));

        Assert.True(session.HasAnnotations);
    }

    [Fact]
    public void Pen_click_without_a_drag_produces_nothing()
    {
        AnnotationEditSession session = NewSession();
        session.SelectTool(ScreenshotTool.Pen);
        session.Begin(new Point(10, 10));
        session.Commit();

        Assert.False(session.HasAnnotations);
    }

    [Fact]
    public void ToLocal_translates_the_committed_mark()
    {
        AnnotationEditSession session = NewSession();
        session.ToLocal = point => new Point(point.X - 100, point.Y - 50);
        DrawDrag(session, ScreenshotTool.Rectangle, new Point(110, 60), new Point(160, 110));

        ScreenshotAnnotation mark = Assert.Single(session.Annotations);
        Assert.Equal(new ScreenshotPointF(10, 10), mark.Points[0]);
        Assert.Equal(new ScreenshotPointF(60, 60), mark.Points[1]);
    }

    [Fact]
    public void ScaleAt_drives_the_stroke_thickness()
    {
        // 2.5 DIP × scale 2 = 5 物理像素（不低于 StrokeFloor=1，再兜底 1）。
        AnnotationEditSession session = new() { ScaleAt = _ => 2f };
        DrawDrag(session, ScreenshotTool.Rectangle, new Point(0, 0), new Point(50, 50));

        Assert.Equal(5f, session.Annotations[0].Thickness);
    }

    [Fact]
    public void Text_anchor_respects_CanPlaceText()
    {
        AnnotationEditSession session = NewSession();
        session.CanPlaceText = _ => false;
        session.SelectTool(ScreenshotTool.Text);

        Assert.False(session.Begin(new Point(10, 10)));
        Assert.Null(session.TextAnchor);
    }

    [Fact]
    public void Blank_text_produces_no_annotation()
    {
        AnnotationEditSession session = NewSession();
        session.SelectTool(ScreenshotTool.Text);
        session.Begin(new Point(10, 10));

        session.CommitText("   ");
        Assert.Null(session.TextAnchor);
        Assert.False(session.HasAnnotations);
    }

    [Fact]
    public void Text_font_size_follows_the_host_scale()
    {
        // 16 DIP × scale 2 = 32 物理像素（下限 10）。
        AnnotationEditSession session = new() { ScaleAt = _ => 2f };
        session.SelectTool(ScreenshotTool.Text);
        session.Begin(new Point(30, 40));
        session.CommitText("你好");

        ScreenshotAnnotation mark = Assert.Single(session.Annotations);
        Assert.Equal(ScreenshotAnnotationKind.Text, mark.Kind);
        Assert.Equal(32f, mark.FontSize);
        Assert.Equal("你好", mark.Text);
    }

    [Fact]
    public void SelectTool_toggles_and_cleaves_the_text_anchor()
    {
        AnnotationEditSession session = NewSession();
        session.SelectTool(ScreenshotTool.Rectangle);
        Assert.Equal(ScreenshotTool.Rectangle, session.Tool);

        session.SelectTool(ScreenshotTool.Text);
        session.Begin(new Point(5, 5));
        Assert.NotNull(session.TextAnchor);

        // 切换工具放弃未提交的文字锚点（与两处宿主的原行为一致）。
        session.SelectTool(ScreenshotTool.Rectangle);
        Assert.Null(session.TextAnchor);

        // 再点一次当前工具 = 取消选择。
        session.SelectTool(ScreenshotTool.Rectangle);
        Assert.Equal(ScreenshotTool.None, session.Tool);
    }

    [Fact]
    public void Session_marks_compose_identically_to_hand_built_ones()
    {
        // 位图级比对：会话走 Begin/Update/Commit 产出的标注，与按同参手工构造的标注
        // 经同一条合成器路径渲染后必须逐像素一致（所见即所得的根本保证）。
        using SKBitmap desktop = Solid(120, 120, SKColors.White);

        AnnotationEditSession session = NewSession();
        DrawDrag(session, ScreenshotTool.Rectangle, new Point(10, 10), new Point(60, 50));
        ScreenshotAnnotation mark = Assert.Single(session.Annotations);

        using SKBitmap viaSession = ScreenshotComposer.Compose(
            desktop, new SKRectI(0, 0, 120, 120), session.Annotations);
        using SKBitmap viaHand = ScreenshotComposer.Compose(
            desktop, new SKRectI(0, 0, 120, 120), new[] { mark });

        AssertSamePixels(viaHand, viaSession);
        Assert.Equal(new ScreenshotPointF(10, 10), mark.Points[0]);
        Assert.Equal(ScreenshotAnnotationKind.Rectangle, mark.Kind);
        Assert.Equal(ScreenshotAnnotation.DefaultColorArgb, mark.ColorArgb);
    }

    [Fact]
    public void Scaled_pen_stroke_composes_identically()
    {
        // 带缩放与多采点的画笔：粗细/坐标换算后同样必须逐像素一致。
        using SKBitmap desktop = Solid(140, 140, SKColors.White);

        AnnotationEditSession session = new() { ScaleAt = _ => 2f, MinPenStep = 5f };
        session.SelectTool(ScreenshotTool.Pen);
        session.Begin(new Point(10, 10));
        session.Update(new Point(60, 20));
        session.Update(new Point(70, 90));
        session.Commit();

        ScreenshotAnnotation mark = Assert.Single(session.Annotations);
        Assert.Equal(3, mark.Points.Count);
        Assert.Equal(5f, mark.Thickness);

        using SKBitmap viaSession = ScreenshotComposer.Compose(
            desktop, new SKRectI(0, 0, 140, 140), session.Annotations);
        using SKBitmap viaHand = ScreenshotComposer.Compose(
            desktop, new SKRectI(0, 0, 140, 140), new[] { mark });

        AssertSamePixels(viaHand, viaSession);
    }

    [Fact]
    public void Text_from_the_session_composes_identically()
    {
        using SKBitmap desktop = Solid(240, 120, SKColors.White);

        AnnotationEditSession session = NewSession();
        session.SelectTool(ScreenshotTool.Text);
        session.Begin(new Point(10, 5));
        session.CommitText("AA\nBB");

        ScreenshotAnnotation mark = Assert.Single(session.Annotations);
        using SKBitmap viaSession = ScreenshotComposer.Compose(
            desktop, new SKRectI(0, 0, 240, 120), session.Annotations);
        using SKBitmap viaHand = ScreenshotComposer.Compose(
            desktop, new SKRectI(0, 0, 240, 120), new[] { mark });

        AssertSamePixels(viaHand, viaSession);
    }

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private static void AssertSamePixels(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        }
    }
}
