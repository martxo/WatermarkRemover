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
}
