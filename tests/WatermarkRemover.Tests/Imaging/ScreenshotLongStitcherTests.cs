using SkiaSharp;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.Tests.Imaging;

/// <summary>
/// Long screenshots are just many frames with a shared overlap; these tests pin the overlap
/// search and the appended height so a failed match cannot silently duplicate content.
/// </summary>
public sealed class ScreenshotLongStitcherTests
{
    private static SKBitmap Frame(int width, int startRow, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            int sourceRow = startRow + y;
            // Every row has a distinct colour so an offset match is unambiguous.
            var color = new SKColor(
                (byte)(sourceRow * 7 % 251),
                (byte)(sourceRow * 13 % 251),
                (byte)(sourceRow * 29 % 251));
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, color);
        }

        return bitmap;
    }

    [Fact]
    public void Finds_the_shared_rows_and_appends_only_the_new_part()
    {
        using var upper = Frame(120, 0, 240);     // source rows 0..239
        using var lower = Frame(120, 160, 240);   // source rows 160..399, overlap 80

        int overlap = ScreenshotLongStitcher.FindOverlap(upper, lower);
        Assert.Equal(80, overlap);

        using var stitched = ScreenshotLongStitcher.Append(upper, lower, overlap);
        Assert.Equal(400, stitched.Height);
    }

    [Fact]
    public void Identical_frames_report_the_full_frame_as_overlap()
    {
        using var frame = Frame(120, 0, 200);

        int overlap = ScreenshotLongStitcher.FindOverlap(frame, frame);
        Assert.Equal(200, overlap);

        using var stitched = ScreenshotLongStitcher.Append(frame, frame, overlap);
        Assert.Equal(200, stitched.Height);
    }

    [Fact]
    public void Unrelated_frames_do_not_stitch()
    {
        using var upper = Frame(120, 0, 200);
        using var unrelated = new SKBitmap(new SKImageInfo(120, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(unrelated))
            canvas.Clear(SKColors.White);

        Assert.Equal(0, ScreenshotLongStitcher.FindOverlap(upper, unrelated));
    }
}
