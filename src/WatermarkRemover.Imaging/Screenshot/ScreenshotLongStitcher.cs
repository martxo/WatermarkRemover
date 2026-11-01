using SkiaSharp;

namespace WatermarkRemover.Imaging.Screenshot;

/// <summary>
/// Stitches manually scrolled frames for long screenshots. Two consecutive captures of the same
/// region share a vertical overlap; finding that overlap and appending only the new rows avoids
/// duplicated content. Matching is sampled around the centre so fixed sidebars/scrollbars do not
/// spoil the result.
/// </summary>
public static class ScreenshotLongStitcher
{
    /// <summary>Stop growing beyond this height so a runaway loop cannot exhaust memory.</summary>
    public const int MaxHeight = 10000;

    private const double AcceptableDifference = 8.0;

    /// <summary>
    /// Rows shared by the bottom of <paramref name="upper"/> and the top of
    /// <paramref name="lower"/>; 0 when the frames do not overlap enough to stitch.
    /// </summary>
    public static int FindOverlap(SKBitmap upper, SKBitmap lower)
    {
        if (upper.Width != lower.Width || upper.Width < 8 || lower.Height < 16)
            return 0;

        SKColor[] topPixels = upper.Pixels;
        SKColor[] bottomPixels = lower.Pixels;

        int margin = upper.Width / 10;
        int xStart = margin;
        int xEnd = upper.Width - margin;
        int stepX = Math.Max(1, (xEnd - xStart) / 160);
        int stepY = Math.Max(1, lower.Height / 160);

        int maxOverlap = Math.Min(upper.Height, lower.Height);
        int minOverlap = Math.Max(16, lower.Height / 10);

        int bestOverlap = 0;
        double bestDifference = double.MaxValue;

        // Coarse search then refine around the winner; full-pixel search over every overlap
        // would make each capture feel slow on 4K selections.
        for (int overlap = maxOverlap; overlap >= minOverlap; overlap -= 4)
        {
            double difference = Difference(upper, topPixels, bottomPixels, overlap,
                xStart, xEnd, stepX, stepY);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestOverlap = overlap;
            }

            if (difference < 1.0) break; // pixel-identical frames
        }

        for (int offset = -3; offset <= 3; offset++)
        {
            int overlap = bestOverlap + offset;
            if (overlap < minOverlap || overlap > maxOverlap) continue;
            double difference = Difference(upper, topPixels, bottomPixels, overlap,
                xStart, xEnd, stepX, stepY);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestOverlap = overlap;
            }
        }

        return bestDifference <= AcceptableDifference ? bestOverlap : 0;
    }

    private static double Difference(
        SKBitmap upper,
        SKColor[] topPixels,
        SKColor[] bottomPixels,
        int overlap,
        int xStart,
        int xEnd,
        int stepX,
        int stepY)
    {
        long sum = 0;
        int samples = 0;
        int upperStartRow = upper.Height - overlap;

        for (int row = 0; row < overlap; row += stepY)
        {
            int upperRow = upperStartRow + row;
            int topOffset = upperRow * upper.Width;
            int bottomOffset = row * upper.Width;
            for (int x = xStart; x < xEnd; x += stepX)
            {
                SKColor a = topPixels[topOffset + x];
                SKColor b = bottomPixels[bottomOffset + x];
                sum += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                samples += 3;
            }
        }

        return samples == 0 ? double.MaxValue : (double)sum / samples;
    }

    /// <summary>Returns a new bitmap with the lower frame's new rows appended under the upper one.</summary>
    public static SKBitmap Append(SKBitmap upper, SKBitmap lower, int overlap)
    {
        int newHeight = upper.Height + lower.Height - overlap;
        var result = new SKBitmap(new SKImageInfo(
            upper.Width, newHeight, upper.ColorType, upper.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(upper, 0, 0);
        canvas.DrawBitmap(
            lower,
            new SKRectI(0, overlap, lower.Width, lower.Height),
            new SKRect(0, upper.Height, upper.Width, newHeight));
        return result;
    }
}
