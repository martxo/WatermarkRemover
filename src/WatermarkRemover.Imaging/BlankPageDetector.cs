namespace WatermarkRemover.Imaging;

/// <summary>
/// Decides whether a scanned page image is blank — the empty back sides a duplex feeder
/// produces when the sheet count runs ahead of the paper, or a feeder misfeed. Judgement is
/// pure pixel statistics (no OCR): ink is anything darker than scanner-grey, and a page with
/// essentially no ink is blank. Fast enough to run on every page of a long scan, and immune
/// to stamps/pictures (those pages carry plenty of ink and are never called blank).
/// </summary>
public static class BlankPageDetector
{
    /// <summary>A page is blank when fewer than this share of its samples carry ink.</summary>
    internal const double BlankInkRatio = 0.005; // 0.5%

    /// <summary>Luminance below this counts as ink — scanner background grey (240+) never does,
    /// while pencil-light text (~120-180) still does.</summary>
    internal const int InkGrayLevel = 200;

    /// <summary>Sampling stride: every Nth pixel in each axis. Blankness is a ratio, so a
    /// ~30x reduction in samples changes nothing about the verdict.</summary>
    private const int SampleStride = 3;

    public static bool IsBlank(byte[] png)
    {
        SkiaSharp.SKBitmap? bitmap;
        try
        {
            bitmap = SkiaSharp.SKBitmap.Decode(png);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            bitmap = null; // SkiaSharp throws (not returns null) on undecodable bytes
        }

        if (bitmap is null) return false; // undecodable → treat as content, never delete blindly
        using (bitmap)
        using (var pixmap = bitmap.PeekPixels())
        {
            if (pixmap is null) return false;

            ReadOnlySpan<byte> bytes = pixmap.GetPixelSpan();
            int width = bitmap.Width;
            int bytesPerPixel = bitmap.BytesPerPixel;
            long sampled = 0, inked = 0;

            for (int y = 0; y < bitmap.Height; y += SampleStride)
            {
                int rowStart = y * pixmap.RowBytes;
                for (int x = 0; x < width; x += SampleStride)
                {
                    int p = rowStart + x * bytesPerPixel;
                    // BGRA layout (Bgra8888): B, G, R, A — luminance from the three channels.
                    int gray = (bytes[p + 2] * 299 + bytes[p + 1] * 587 + bytes[p] * 114) / 1000;
                    sampled++;
                    if (gray < InkGrayLevel) inked++;
                }
            }

            return sampled == 0 || inked / (double)sampled < BlankInkRatio;
        }
    }
}
