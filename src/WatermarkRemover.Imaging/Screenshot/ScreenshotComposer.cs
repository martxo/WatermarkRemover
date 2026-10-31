using SkiaSharp;

namespace WatermarkRemover.Imaging.Screenshot;

/// <summary>
/// Turns the captured desktop plus the user's annotations into the final screenshot bitmap.
/// Pure image work (no WPF): the overlay previews the same result it later saves and copies,
/// so what the user sees at 1:1 is exactly the PNG on disk.
/// </summary>
public static class ScreenshotComposer
{
    /// <summary>Mosaic block size in physical pixels; larger blocks hide more text.</summary>
    private const int MosaicBlock = 12;

    /// <summary>The text font used for 文字 annotations; falls back to Skia's default.</summary>
    private const string TextFontFamily = "Microsoft YaHei";

    /// <summary>
    /// Crops <paramref name="desktop"/> to <paramref name="crop"/> and draws every annotation.
    /// The returned bitmap is BGRA premultiplied and owned by the caller.
    /// </summary>
    public static SKBitmap Compose(SKBitmap desktop, SKRectI crop, IReadOnlyList<ScreenshotAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(annotations);

        var clamped = new SKRectI(
            Math.Max(crop.Left, 0),
            Math.Max(crop.Top, 0),
            Math.Min(crop.Right, desktop.Width),
            Math.Min(crop.Bottom, desktop.Height));
        if (clamped.Width <= 0 || clamped.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(crop), "选区超出了屏幕范围");

        var output = new SKBitmap(new SKImageInfo(clamped.Width, clamped.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);

        var source = new SKRect(clamped.Left, clamped.Top, clamped.Right, clamped.Bottom);
        var destination = new SKRect(0, 0, clamped.Width, clamped.Height);
        canvas.DrawBitmap(desktop, source, destination);

        foreach (ScreenshotAnnotation annotation in annotations)
            Draw(canvas, output, annotation);

        canvas.Flush();
        return output;
    }

    private static void Draw(SKCanvas canvas, SKBitmap current, ScreenshotAnnotation annotation)
    {
        SKColor color = new(annotation.ColorArgb);
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            StrokeWidth = Math.Max(1, annotation.Thickness),
        };

        switch (annotation.Kind)
        {
            case ScreenshotAnnotationKind.Rectangle:
                DrawRectangle(canvas, annotation, paint);
                break;

            case ScreenshotAnnotationKind.Arrow:
                DrawArrow(canvas, annotation, paint);
                break;

            case ScreenshotAnnotationKind.Pen:
                DrawPen(canvas, annotation, paint);
                break;

            case ScreenshotAnnotationKind.Text:
                DrawText(canvas, annotation, paint);
                break;

            case ScreenshotAnnotationKind.Mosaic:
                DrawMosaic(canvas, current, annotation);
                break;
        }
    }

    private static SKRect BoundsOf(ScreenshotAnnotation annotation) => annotation.Points.Count == 0
        ? SKRect.Empty
        : new SKRect(
            Math.Min(annotation.Points[0].X, annotation.Points[^1].X),
            Math.Min(annotation.Points[0].Y, annotation.Points[^1].Y),
            Math.Max(annotation.Points[0].X, annotation.Points[^1].X),
            Math.Max(annotation.Points[0].Y, annotation.Points[^1].Y));

    private static void DrawRectangle(SKCanvas canvas, ScreenshotAnnotation annotation, SKPaint paint)
    {
        SKRect rect = BoundsOf(annotation);
        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawRect(rect, paint);
    }

    private static void DrawArrow(SKCanvas canvas, ScreenshotAnnotation annotation, SKPaint paint)
    {
        if (annotation.Points.Count < 2) return;

        var start = new SKPoint(annotation.Points[0].X, annotation.Points[0].Y);
        var end = new SKPoint(annotation.Points[^1].X, annotation.Points[^1].Y);
        float thickness = Math.Max(1, annotation.Thickness);

        paint.Style = SKPaintStyle.Stroke;
        canvas.DrawLine(start, end, paint);

        float angle = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        float head = Math.Max(8, thickness * 3.2f);
        float spread = MathF.PI / 7;
        var tip1 = new SKPoint(
            end.X - head * MathF.Cos(angle - spread),
            end.Y - head * MathF.Sin(angle - spread));
        var tip2 = new SKPoint(
            end.X - head * MathF.Cos(angle + spread),
            end.Y - head * MathF.Sin(angle + spread));

        paint.Style = SKPaintStyle.Fill;
        using var headPath = new SKPath();
        headPath.MoveTo(end);
        headPath.LineTo(tip1);
        headPath.LineTo(tip2);
        headPath.Close();
        canvas.DrawPath(headPath, paint);
    }

    private static void DrawPen(SKCanvas canvas, ScreenshotAnnotation annotation, SKPaint paint)
    {
        if (annotation.Points.Count < 2) return;

        using var path = new SKPath();
        path.MoveTo(annotation.Points[0].X, annotation.Points[0].Y);
        for (int i = 1; i < annotation.Points.Count; i++)
            path.LineTo(annotation.Points[i].X, annotation.Points[i].Y);

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        canvas.DrawPath(path, paint);
    }

    private static void DrawText(SKCanvas canvas, ScreenshotAnnotation annotation, SKPaint paint)
    {
        if (annotation.Points.Count == 0 || string.IsNullOrEmpty(annotation.Text)) return;

        using var typeface = SKTypeface.FromFamilyName(TextFontFamily, SKFontStyle.Normal) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, Math.Max(8, annotation.FontSize));

        paint.Style = SKPaintStyle.Fill;
        var anchor = annotation.Points[0];
        // The anchor is the text box's top-left; Skia draws from the baseline.
        canvas.DrawText(annotation.Text, anchor.X, anchor.Y + font.Size, font, paint);
    }

    /// <summary>
    /// Pixelates the marked region. The block grid is aligned to the region itself, and the
    /// snapshot is taken before the pixels are painted back, so overlapping mosaics stack.
    /// </summary>
    private static void DrawMosaic(SKCanvas canvas, SKBitmap current, ScreenshotAnnotation annotation)
    {
        SKRect bounds = BoundsOf(annotation);
        var rect = new SKRect(
            Math.Max(bounds.Left, 0),
            Math.Max(bounds.Top, 0),
            Math.Min(bounds.Right, current.Width),
            Math.Min(bounds.Bottom, current.Height));
        if (rect.Width < 2 || rect.Height < 2) return;

        int width = Math.Max(1, (int)MathF.Round(rect.Width / MosaicBlock));
        int height = Math.Max(1, (int)MathF.Round(rect.Height / MosaicBlock));

        using var small = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var smallCanvas = new SKCanvas(small))
        {
            smallCanvas.DrawBitmap(current, rect, new SKRect(0, 0, width, height));
        }

        canvas.DrawBitmap(small, new SKRect(0, 0, width, height), rect);
    }
}
