using SkiaSharp;

namespace WatermarkRemover.App.Imaging;

/// <summary>How aggressively a photo is cut down to its document.</summary>
public enum CropMode
{
    /// <summary>Cut on a non-paper background; leave an already page-like white scan untouched.</summary>
    Auto,

    /// <summary>Always cut to the content bounds, even on a white background.</summary>
    Content,

    /// <summary>Leave the image exactly as received.</summary>
    None,
}

/// <param name="Mode">See <see cref="CropMode"/>.</param>
/// <param name="PaddingRatio">Breathing room around the detected content, as a fraction of the
/// shorter side (1.5% keeps a card's printed edge from touching the composite's cell border).</param>
/// <param name="Tolerance">Per-channel colour tolerance when deciding what still counts as the
/// background. 26 absorbs JPEG noise and sensor grain without swallowing a light card edge.</param>
public sealed record CropSettings(CropMode Mode = CropMode.Auto, double PaddingRatio = 0.015, int Tolerance = 26);

/// <summary>
/// Generic document cutter for photos arriving from a phone. It makes no assumption about the
/// document's style: the background is whatever uniform colour borders the frame, and the
/// content is everything connected to the page that is not that colour. The only guard is
/// safety — a result that keeps less than 3% or almost all of the frame is discarded, so a
/// full-bleed scan or a busy photo is never sliced to a speck.
/// </summary>
public static class DocumentCropper
{
    /// <summary>Longest side used for the background analysis; the crop itself is applied to the
    /// full-resolution pixels sampled at this grid's step.</summary>
    private const int AnalysisSide = 420;

    private const double MinContentRatio = 0.03;
    private const double MaxContentRatio = 0.985;

    /// <summary>Cuts <paramref name="source"/> and returns PNG bytes. When nothing is worth
    /// cutting (already a page, unreadable, or a degenerate result) the original bytes come back
    /// untouched — no needless re-encode.</summary>
    public static byte[] Crop(byte[] source, CropSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new CropSettings();

        try
        {
            using SKBitmap? decoded = SKBitmap.Decode(source);
            if (decoded is null) return source;

            // Nothing worth cutting → hand back the original bytes (no re-encode, no quality loss).
            if (FindContentBounds(decoded, settings) is not { } subset) return source;

            var cropped = new SKBitmap();
            if (!decoded.ExtractSubset(cropped, subset))
            {
                cropped.Dispose();
                return source;
            }

            using (cropped)
            {
                using SKImage image = SKImage.FromBitmap(cropped);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
        }
        catch
        {
            // A cutter must never take a transfer down; the untouched file is always valid.
            return source;
        }
    }

    /// <summary>Cuts a decoded bitmap. The returned bitmap is always a new instance the caller
    /// owns — either a subset or a copy of the original when nothing is worth cutting.</summary>
    public static SKBitmap CropBitmap(SKBitmap source, CropSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new CropSettings();

        if (settings.Mode == CropMode.None) return source.Copy();

        SKRectI? bounds = FindContentBounds(source, settings);
        if (bounds is not { } subset) return source.Copy();

        var cropped = new SKBitmap();
        return source.ExtractSubset(cropped, subset) ? cropped : source.Copy();
    }

    /// <summary>Content rectangle in full-resolution pixels, or null when the frame should be
    /// kept whole (paper-white scan under Auto, unreadable geometry, or a degenerate result).</summary>
    internal static SKRectI? FindContentBounds(SKBitmap source, CropSettings settings)
    {
        int width = source.Width;
        int height = source.Height;
        if (width < 8 || height < 8) return null;

        int step = Math.Max(1, Math.Max(width, height) / AnalysisSide);
        int gw = (width + step - 1) / step;
        int gh = (height + step - 1) / step;
        if (gw < 3 || gh < 3) return null;

        var colors = new SKColor[gw * gh];
        for (int gy = 0; gy < gh; gy++)
        {
            int y = Math.Min(height - 1, gy * step);
            for (int gx = 0; gx < gw; gx++)
                colors[gy * gw + gx] = source.GetPixel(Math.Min(width - 1, gx * step), y);
        }

        SKColor background = DominantBorderColor(colors, gw, gh);

        // Scans and screenshots are already cropped by the producing app; cutting their white
        // margins would silently resize a page whose geometry (e.g. a credit report header) the
        // downstream pipeline relies on. Only an actual photo background is auto-cut.
        if (settings.Mode == CropMode.Auto && IsPaperWhite(background)) return null;

        int toleranceSquared = settings.Tolerance * settings.Tolerance;

        var isBackground = new bool[colors.Length];
        var queue = new Queue<int>();

        void TrySeed(int index)
        {
            if (isBackground[index]) return;
            if (DistanceSquared(colors[index], background) > toleranceSquared) return;
            isBackground[index] = true;
            queue.Enqueue(index);
        }

        for (int gx = 0; gx < gw; gx++)
        {
            TrySeed(gx);
            TrySeed((gh - 1) * gw + gx);
        }
        for (int gy = 0; gy < gh; gy++)
        {
            TrySeed(gy * gw);
            TrySeed(gy * gw + gw - 1);
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % gw;
            int y = index / gw;
            if (x > 0) TrySeed(index - 1);
            if (x + 1 < gw) TrySeed(index + 1);
            if (y > 0) TrySeed(index - gw);
            if (y + 1 < gh) TrySeed(index + gw);
        }

        int minX = gw, minY = gh, maxX = -1, maxY = -1;
        for (int gy = 0; gy < gh; gy++)
        {
            for (int gx = 0; gx < gw; gx++)
            {
                if (isBackground[gy * gw + gx]) continue;
                if (gx < minX) minX = gx;
                if (gx > maxX) maxX = gx;
                if (gy < minY) minY = gy;
                if (gy > maxY) maxY = gy;
            }
        }
        if (maxX < 0) return null;

        double ratio = (maxX - minX + 1) * (double)(maxY - minY + 1) / (gw * (double)gh);
        if (ratio < MinContentRatio || ratio > MaxContentRatio) return null;

        int x0 = minX * step;
        int y0 = minY * step;
        int x1 = Math.Min(width, (maxX + 1) * step);
        int y1 = Math.Min(height, (maxY + 1) * step);

        int padding = (int)Math.Round(Math.Min(width, height) * Math.Max(0, settings.PaddingRatio));
        x0 = Math.Max(0, x0 - padding);
        y0 = Math.Max(0, y0 - padding);
        x1 = Math.Min(width, x1 + padding);
        y1 = Math.Min(height, y1 + padding);

        int cropWidth = x1 - x0;
        int cropHeight = y1 - y0;
        if (cropWidth < 16 || cropHeight < 16) return null;

        return new SKRectI(x0, y0, x1, y1);
    }

    /// <summary>The most common quantised colour along the frame's perimeter — the background
    /// even when a document touches one edge (the remaining three still vote).</summary>
    private static SKColor DominantBorderColor(SKColor[] colors, int gw, int gh)
    {
        var bins = new Dictionary<int, (int Count, long R, long G, long B)>();

        void Add(SKColor color)
        {
            int key = (color.Red >> 4) << 8 | (color.Green >> 4) << 4 | (color.Blue >> 4);
            bins.TryGetValue(key, out var bin);
            bins[key] = (bin.Count + 1, bin.R + color.Red, bin.G + color.Green, bin.B + color.Blue);
        }

        for (int gx = 0; gx < gw; gx++)
        {
            Add(colors[gx]);
            Add(colors[(gh - 1) * gw + gx]);
        }
        for (int gy = 0; gy < gh; gy++)
        {
            Add(colors[gy * gw]);
            Add(colors[gy * gw + gw - 1]);
        }

        var best = bins.Values.OrderByDescending(b => b.Count).First();
        return new SKColor(
            (byte)(best.R / best.Count),
            (byte)(best.G / best.Count),
            (byte)(best.B / best.Count));
    }

    private static bool IsPaperWhite(SKColor color)
    {
        int min = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        int max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        return min >= 225 && max - min <= 14;
    }

    private static int DistanceSquared(SKColor a, SKColor b)
    {
        int dr = a.Red - b.Red;
        int dg = a.Green - b.Green;
        int db = a.Blue - b.Blue;
        return dr * dr + dg * dg + db * db;
    }
}
