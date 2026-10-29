using SkiaSharp;

namespace WatermarkRemover.Imaging;

/// <summary>
/// Packs document images into one page per layout template, with no document-type knowledge:
/// each image keeps its own aspect ratio, the page is sized to the content (so two ID sides
/// produce a tight strip rather than a half-empty A4 sheet), and the background is painted
/// white. Pages are JPEG, ready for <c>JpegPdfAssembler</c> to embed verbatim.
/// </summary>
public static class ImagePageComposer
{
    /// <summary>Longest page side; 2480 px ≈ A4 at 300 dpi and keeps PDFs a sane size.</summary>
    public const int DefaultMaxLongSide = 2480;

    private const int JpegQuality = 92;

    /// <summary>Gap and outer margin, as a fraction of the content's longer side.</summary>
    private const double GapRatio = 0.022;

    // --- 身份证 template geometry: real-world millimetres, rasterised at 300 dpi. ---
    private const int A4WidthPx = 2480;
    private const int A4HeightPx = 3508;
    private const double PixelsPerMm = 300.0 / 25.4;
    private const double IdCardWidthMm = 85.6;
    private const double IdCardHeightMm = 54.0;
    private const double A4HeightMm = 297.0;

    /// <summary>Each card is centred inside its own half of the sheet: the upper half for the
    /// front, the lower half for the back. Which side a client photographed first does not
    /// matter — either face centred in its half reads correctly on the printed copy.</summary>
    private const double FrontTopMm = A4HeightMm / 4 - IdCardHeightMm / 2;
    private const double BackTopMm = A4HeightMm * 3 / 4 - IdCardHeightMm / 2;

    /// <summary>
    /// Composes the selection into consecutive pages: a row/column/grid template holds its
    /// <see cref="ComposeLayout.Capacity"/> images per page, so selecting five 2-up images
    /// yields one three-page PDF, not five files.
    /// </summary>
    public static IReadOnlyList<byte[]> ComposePages(
        IReadOnlyList<byte[]> images,
        ComposeLayout layout,
        CropSettings? crop = null,
        int maxLongSide = DefaultMaxLongSide)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(layout);
        if (images.Count == 0) throw new ArgumentException("没有可拼版的图片", nameof(images));
        if (layout.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(layout));

        int capacity = Math.Min(layout.Capacity, images.Count);
        var pages = new List<byte[]>((images.Count + capacity - 1) / capacity);
        for (int start = 0; start < images.Count; start += capacity)
        {
            int take = Math.Min(capacity, images.Count - start);
            var chunk = new byte[take][];
            for (int i = 0; i < take; i++) chunk[i] = images[start + i];
            pages.Add(ComposePage(chunk, layout, crop, maxLongSide));
        }
        return pages;
    }

    /// <summary>One page from at most <see cref="ComposeLayout.Capacity"/> images.</summary>
    public static byte[] ComposePage(
        IReadOnlyList<byte[]> images,
        ComposeLayout layout,
        CropSettings? crop = null,
        int maxLongSide = DefaultMaxLongSide)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(layout);
        if (images.Count == 0) throw new ArgumentException("没有可拼版的图片", nameof(images));
        if (layout.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(layout));

        var decoded = new List<SKBitmap>(images.Count);
        try
        {
            foreach (byte[] bytes in images)
            {
                SKBitmap? bitmap = SKBitmap.Decode(bytes)
                    ?? throw new InvalidOperationException("图片无法解码，可能格式不受支持");
                decoded.Add(DocumentCropper.CropBitmap(bitmap, crop));
                bitmap.Dispose();
            }

            return ComposeDecoded(decoded, layout.Kind, maxLongSide);
        }
        finally
        {
            foreach (SKBitmap bitmap in decoded) bitmap.Dispose();
        }
    }

    private static byte[] ComposeDecoded(IReadOnlyList<SKBitmap> images, ComposeLayoutKind kind, int maxLongSide)
    {
        if (kind == ComposeLayoutKind.IdCardA4) return ComposeIdCardA4(images);

        int count = images.Count;
        int gap = Math.Max(8, (int)Math.Round(images.Max(b => Math.Max(b.Width, b.Height)) * GapRatio));
        int margin = gap;

        var placements = new List<(int X, int Y, int W, int H)>(count);
        int contentWidth;
        int contentHeight;

        switch (kind)
        {
            case ComposeLayoutKind.Single:
            {
                SKBitmap only = images[0];
                placements.Add((0, 0, only.Width, only.Height));
                contentWidth = only.Width;
                contentHeight = only.Height;
                margin = 0;
                gap = 0;
                break;
            }
            case ComposeLayoutKind.Row:
            {
                int targetHeight = images.Max(b => b.Height);
                int x = 0;
                foreach (SKBitmap image in images)
                {
                    int width = Math.Max(1, (int)Math.Round(image.Width * (double)targetHeight / image.Height));
                    placements.Add((x, 0, width, targetHeight));
                    x += width + gap;
                }
                contentWidth = x - gap;
                contentHeight = targetHeight;
                break;
            }
            case ComposeLayoutKind.Column:
            {
                int targetWidth = images.Max(b => b.Width);
                int y = 0;
                foreach (SKBitmap image in images)
                {
                    int height = Math.Max(1, (int)Math.Round(image.Height * (double)targetWidth / image.Width));
                    placements.Add((0, y, targetWidth, height));
                    y += height + gap;
                }
                contentWidth = targetWidth;
                contentHeight = y - gap;
                break;
            }
            default: // Grid — two columns of equal cells, images centred inside their cell.
            {
                int cellWidth = images.Max(b => b.Width);
                int cellHeight = images.Max(b => b.Height);
                int columns = Math.Min(2, count);
                int rows = (count + columns - 1) / columns;
                for (int i = 0; i < count; i++)
                {
                    int row = i / columns;
                    int column = i % columns;
                    SKBitmap image = images[i];
                    double fit = Math.Min(cellWidth / (double)image.Width, cellHeight / (double)image.Height);
                    int width = Math.Max(1, (int)Math.Round(image.Width * fit));
                    int height = Math.Max(1, (int)Math.Round(image.Height * fit));
                    int cellX = column * (cellWidth + gap);
                    int cellY = row * (cellHeight + gap);
                    placements.Add((
                        cellX + (cellWidth - width) / 2,
                        cellY + (cellHeight - height) / 2,
                        width,
                        height));
                }
                contentWidth = columns * cellWidth + (columns - 1) * gap;
                contentHeight = rows * cellHeight + (rows - 1) * gap;
                break;
            }
        }

        int canvasWidth = contentWidth + 2 * margin;
        int canvasHeight = contentHeight + 2 * margin;
        double scale = Math.Min(1.0, maxLongSide / (double)Math.Max(1, Math.Max(canvasWidth, canvasHeight)));
        canvasWidth = Math.Max(1, (int)Math.Round(canvasWidth * scale));
        canvasHeight = Math.Max(1, (int)Math.Round(canvasHeight * scale));

        using var canvasBitmap = new SKBitmap(new SKImageInfo(
            canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.White);
            for (int i = 0; i < images.Count; i++)
            {
                var cell = placements[i];
                int x = (int)Math.Round(cell.X * scale) + (int)Math.Round(margin * scale);
                int y = (int)Math.Round(cell.Y * scale) + (int)Math.Round(margin * scale);
                int width = Math.Max(1, (int)Math.Round(cell.W * scale));
                int height = Math.Max(1, (int)Math.Round(cell.H * scale));

                using SKBitmap scaled = ScaleTo(images[i], width, height);
                canvas.DrawBitmap(scaled, x, y);
            }
        }

        return EncodeJpeg(canvasBitmap);
    }

    /// <summary>
    /// The 身份证 sheet: A4 portrait at 300 dpi, two cards at their physical 85.6×54 mm, each
    /// centred in its half (front upper, back lower), horizontally centred. A portrait photo is
    /// turned 90° first (ID cards are always landscape); whichever image does not fill the card
    /// box is centred in it, so the printed outline is exactly the real card size.
    /// </summary>
    private static byte[] ComposeIdCardA4(IReadOnlyList<SKBitmap> images)
    {
        int cardWidth = (int)Math.Round(IdCardWidthMm * PixelsPerMm);
        int cardHeight = (int)Math.Round(IdCardHeightMm * PixelsPerMm);
        int left = (A4WidthPx - cardWidth) / 2;
        int[] tops =
        {
            (int)Math.Round(FrontTopMm * PixelsPerMm),
            (int)Math.Round(BackTopMm * PixelsPerMm),
        };

        using var canvasBitmap = new SKBitmap(new SKImageInfo(
            A4WidthPx, A4HeightPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.White);
            for (int i = 0; i < images.Count && i < tops.Length; i++)
            {
                SKBitmap image = images[i];
                bool rotate = image.Height > image.Width;
                int sourceWidth = rotate ? image.Height : image.Width;
                int sourceHeight = rotate ? image.Width : image.Height;

                double fit = Math.Min(cardWidth / (double)sourceWidth, cardHeight / (double)sourceHeight);
                int drawWidth = Math.Max(1, (int)Math.Round(sourceWidth * fit));
                int drawHeight = Math.Max(1, (int)Math.Round(sourceHeight * fit));
                int x = left + (cardWidth - drawWidth) / 2;
                int y = tops[i] + (cardHeight - drawHeight) / 2;

                if (!rotate)
                {
                    using SKBitmap scaled = ScaleTo(image, drawWidth, drawHeight);
                    canvas.DrawBitmap(scaled, x, y);
                    continue;
                }

                // A sideways photo: scale to the rotated footprint, then turn it clockwise into
                // the landscape card box.
                using SKBitmap rotated = ScaleTo(image, drawHeight, drawWidth);
                canvas.Save();
                canvas.Translate(x + drawWidth, y);
                canvas.RotateDegrees(90);
                canvas.DrawBitmap(rotated, 0, 0);
                canvas.Restore();
            }
        }

        return EncodeJpeg(canvasBitmap);
    }

    private static byte[] EncodeJpeg(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }

    /// <summary>High-quality resize into the exact cell size; a cell that already matches the
    /// source avoids resampling entirely.</summary>
    private static SKBitmap ScaleTo(SKBitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height) return source.Copy();

        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell)))
            return target;

        target.Dispose();
        return source.Copy();
    }
}
