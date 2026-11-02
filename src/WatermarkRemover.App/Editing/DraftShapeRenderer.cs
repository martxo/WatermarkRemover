using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App.Editing;

/// <summary>
/// 把拖拽中的草稿标注画到 WPF 图层上——截图遮罩与标注窗共用的实时预览形状。
/// 宿主只提供「画布坐标 → 显示坐标」的换算与描边粗细（DIP），几何与箭头头部算法集中在这里，
/// 和 <see cref="WatermarkRemover.Imaging.Screenshot.ScreenshotComposer"/> 的最终绘制保持一致。
/// </summary>
internal static class DraftShapeRenderer
{
    /// <summary>各宿主的草稿观感（马赛克草稿的填充/描沿按底色分别调过）。</summary>
    public readonly record struct Options(
        Color Color,
        double StrokeDip,
        Color MosaicFill,
        Color MosaicStroke,
        DoubleCollection? MosaicDash);

    /// <summary>ARGB（0xAARRGGBB）转 WPF 颜色。</summary>
    public static Color MarkColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>重画草稿；点数不足 2 时清空图层（与两个宿主的原有守卫一致）。</summary>
    public static void Render(
        Panel layer, ScreenshotAnnotation draft, Func<Point, Point> toDisplay, in Options options)
    {
        layer.Children.Clear();
        if (draft.Points.Count < 2) return;

        var brush = new SolidColorBrush(options.Color);
        Point first = toDisplay(new Point(draft.Points[0].X, draft.Points[0].Y));
        Point last = toDisplay(new Point(draft.Points[^1].X, draft.Points[^1].Y));

        switch (draft.Kind)
        {
            case ScreenshotAnnotationKind.Pen:
            {
                var polyline = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = options.StrokeDip,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                foreach (ScreenshotPointF point in draft.Points)
                    polyline.Points.Add(toDisplay(new Point(point.X, point.Y)));
                layer.Children.Add(polyline);
                break;
            }

            case ScreenshotAnnotationKind.Arrow:
            {
                layer.Children.Add(new Line
                {
                    X1 = first.X, Y1 = first.Y, X2 = last.X, Y2 = last.Y,
                    Stroke = brush,
                    StrokeThickness = options.StrokeDip,
                });
                layer.Children.Add(ArrowHead(first, last, options.StrokeDip, brush));
                break;
            }

            case ScreenshotAnnotationKind.Mosaic:
            {
                var rect = new Rectangle
                {
                    Stroke = new SolidColorBrush(options.MosaicStroke),
                    StrokeThickness = 1,
                    StrokeDashArray = options.MosaicDash,
                    Fill = new SolidColorBrush(options.MosaicFill),
                    Width = Math.Abs(last.X - first.X),
                    Height = Math.Abs(last.Y - first.Y),
                };
                Canvas.SetLeft(rect, Math.Min(first.X, last.X));
                Canvas.SetTop(rect, Math.Min(first.Y, last.Y));
                layer.Children.Add(rect);
                break;
            }

            default:
            {
                var rect = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = options.StrokeDip,
                    Width = Math.Abs(last.X - first.X),
                    Height = Math.Abs(last.Y - first.Y),
                };
                Canvas.SetLeft(rect, Math.Min(first.X, last.X));
                Canvas.SetTop(rect, Math.Min(first.Y, last.Y));
                layer.Children.Add(rect);
                break;
            }
        }
    }

    /// <summary>三角箭头头部，尺寸与合成器同比例（max(10, 粗细×3.2)，张角 π/7）。</summary>
    private static Polygon ArrowHead(Point start, Point end, double thickness, Brush brush)
    {
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double head = Math.Max(10, thickness * 3.2);
        double spread = Math.PI / 7;
        return new Polygon
        {
            Fill = brush,
            Points =
            {
                end,
                new Point(
                    end.X - head * Math.Cos(angle - spread),
                    end.Y - head * Math.Sin(angle - spread)),
                new Point(
                    end.X - head * Math.Cos(angle + spread),
                    end.Y - head * Math.Sin(angle + spread)),
            },
        };
    }
}
