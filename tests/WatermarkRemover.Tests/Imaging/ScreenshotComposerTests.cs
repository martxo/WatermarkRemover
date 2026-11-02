using SkiaSharp;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.Tests.Imaging;

/// <summary>
/// The composer is the only path from "what the overlay shows" to "the PNG the user keeps", so
/// its crop geometry and annotation painting are pinned here.
/// </summary>
public sealed class ScreenshotComposerTests
{
    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    [Fact]
    public void Compose_crops_to_the_selection_size()
    {
        using var desktop = Solid(100, 80, new SKColor(120, 30, 200));
        using var output = ScreenshotComposer.Compose(
            desktop, new SKRectI(20, 10, 60, 40), Array.Empty<ScreenshotAnnotation>());

        Assert.Equal(40, output.Width);
        Assert.Equal(30, output.Height);
        Assert.Equal(new SKColor(120, 30, 200), output.GetPixel(0, 0));
    }

    [Fact]
    public void Rectangle_is_painted_in_the_annotation_colour()
    {
        using var desktop = Solid(80, 80, SKColors.White);
        var annotations = new[]
        {
            ScreenshotAnnotation.Rectangle(10, 10, 60, 50, 3),
        };

        using var output = ScreenshotComposer.Compose(desktop, new SKRectI(0, 0, 80, 80), annotations);
        SKColor pixel = output.GetPixel(10, 10);

        Assert.True(pixel.Red > 150, $"expected red stroke, got {pixel}");
        Assert.True(pixel.Green < 120, $"expected red stroke, got {pixel}");
    }

    [Fact]
    public void Mosaic_blurs_a_region_and_leaves_the_rest_alone()
    {
        using var desktop = new SKBitmap(new SKImageInfo(96, 96, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(desktop))
        {
            for (int y = 0; y < 96; y++)
            {
                for (int x = 0; x < 96; x++)
                    canvas.DrawPoint(x, y, new SKPaint { Color = new SKColor((byte)(x * 2), (byte)(y * 2), 64) });
            }
        }

        var mosaic = new[]
        {
            ScreenshotAnnotation.Mosaic(20, 20, 70, 70, 3),
        };
        using var output = ScreenshotComposer.Compose(desktop, new SKRectI(0, 0, 96, 96), mosaic);

        // Two points inside one mosaic block must share the block's average colour (the
        // 50px-wide region is split into four 12.5px blocks; both points sit in the first).
        Assert.Equal(output.GetPixel(22, 22), output.GetPixel(28, 28));
        // A pixel well outside the marked region keeps the desktop colour.
        Assert.Equal(desktop.GetPixel(90, 90), output.GetPixel(90, 90));
    }

    [Fact]
    public void Multi_line_text_paints_every_line()
    {
        using var desktop = Solid(240, 120, SKColors.White);
        var text = new[]
        {
            new ScreenshotAnnotation
            {
                Kind = ScreenshotAnnotationKind.Text,
                Points = new[] { new ScreenshotPointF(10, 5) },
                Text = "AA\nBB",
                FontSize = 24,
                ColorArgb = 0xFFE53935,
            },
        };

        using var output = ScreenshotComposer.Compose(desktop, new SKRectI(0, 0, 240, 120), text);

        Assert.True(HasMarkInBand(output, 20, 42), "first line was not painted");
        Assert.True(HasMarkInBand(output, 50, 78), "second line was not painted");
    }

    private static bool HasMarkInBand(SKBitmap bitmap, int top, int bottom)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = 5; x < 200; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 150 && pixel.Green < 150) return true;
            }
        }

        return false;
    }

    [Fact]
    public void Arrow_line_contains_the_mark_colour()
    {
        using var desktop = Solid(120, 120, SKColors.White);
        var arrow = new[]
        {
            ScreenshotAnnotation.Arrow(20, 20, 100, 100, 4),
        };
        using var output = ScreenshotComposer.Compose(desktop, new SKRectI(0, 0, 120, 120), arrow);

        SKColor middle = output.GetPixel(60, 60);
        Assert.True(middle.Red > 150 && middle.Green < 120, $"expected red arrow, got {middle}");
    }

    [Fact]
    public void Annotation_layer_paints_only_the_marks_on_transparency()
    {
        var mark = new[]
        {
            new ScreenshotAnnotation
            {
                Kind = ScreenshotAnnotationKind.Rectangle,
                Points = new[] { new ScreenshotPointF(10, 10), new ScreenshotPointF(60, 60) },
                ColorArgb = 0xFF1E88E5,
                Thickness = 8f,
            },
        };

        using var layer = ScreenshotComposer.ComposeAnnotationLayer(100, 100, mark);

        // 标注层是 PDF「保留原文 + 叠加批注」档的载荷：除了笔迹，全透明。
        Assert.True(layer.GetPixel(95, 95).Alpha == 0, $"corner must be transparent, got {layer.GetPixel(95, 95)}");
        Assert.True(layer.GetPixel(35, 35).Alpha == 0, $"inside must be transparent, got {layer.GetPixel(35, 35)}"); // 矩形内部也透明
        SKColor stroke = layer.GetPixel(10, 35);
        Assert.True(stroke.Alpha > 200, $"stroke must be opaque, got {stroke}");
        Assert.True(stroke.Blue > 150, $"stroke must be the mark colour, got {stroke}");
    }

    [Fact]
    public void Annotation_layer_mosaic_samples_the_page_render_not_transparency()
    {
        // 马赛克要从页面渲染图取样再画进透明层——叠回页面后才和预览看到的像素化一致。
        using var page = Solid(100, 100, new SKColor(30, 160, 60));
        var mosaic = new[]
        {
            ScreenshotAnnotation.Mosaic(20, 20, 60, 60, 3),
        };

        using var layer = ScreenshotComposer.ComposeAnnotationLayer(100, 100, mosaic, page);

        SKColor inside = layer.GetPixel(40, 40);
        Assert.True(inside.Alpha > 200, $"mosaic block must be opaque, got {inside}");
        Assert.True(inside.Green > 120, $"mosaic must carry the page colour, got {inside}");
        Assert.True(layer.GetPixel(80, 80).Alpha == 0, $"outside must be transparent, got {layer.GetPixel(80, 80)}");
    }

    [Fact]
    public void Annotation_layer_without_a_mosaic_source_stays_transparent_under_mosaic()
    {
        // 没有页面渲染图可取样时，马赛克区域保持透明（不画假块）——调用方要么给图，要么别用马赛克。
        var mosaic = new[]
        {
            ScreenshotAnnotation.Mosaic(20, 20, 60, 60, 3),
        };

        using var layer = ScreenshotComposer.ComposeAnnotationLayer(100, 100, mosaic);

        Assert.True(layer.GetPixel(40, 40).Alpha == 0, $"mosaic area must stay transparent, got {layer.GetPixel(40, 40)}");
    }
}
