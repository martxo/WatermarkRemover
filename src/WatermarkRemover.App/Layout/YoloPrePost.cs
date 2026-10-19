using System.IO;
using SkiaSharp;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.App.Layout;

/// <summary>
/// Pre/post-processing for the DocLayout-YOLO ONNX export, mirroring the repository's official
/// <c>inference.py</c> sample line for line: stride-aligned resize + gray-114 pad with the
/// sample's BGR channel order and /255 scaling, then confidence (column 4) &gt; 0.25 filtering
/// and <c>scale_boxes</c> coordinate recovery back to the original image.
/// </summary>
public static class YoloPrePost
{
    public const int Stride = 32;
    public const float ConfThreshold = 0.25f;

    /// <summary>Model input tensor plus the geometry needed to map boxes back to the source.</summary>
    public sealed record Letterboxed(
        float[] Tensor,
        int TensorWidth,
        int TensorHeight,
        int OriginalWidth,
        int OriginalHeight);

    /// <summary>
    /// Builds the [1,3,H,W] float tensor: scale the longest side into a stride-aligned square,
    /// pad the remainder centered with gray 114 (cv2.copyMakeBorder equivalent), read pixels as
    /// BGR and divide by 255 — exactly what the official sample feeds the "images" input.
    /// </summary>
    public static Letterboxed Letterbox(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int origW = source.Width, origH = source.Height;
        int target = Math.Max(Stride, origH / Stride * Stride);

        double ratio = Math.Min((double)target / origH, (double)target / origW);
        int resizedW = Math.Max(1, (int)Math.Round(origW * ratio));
        int resizedH = Math.Max(1, (int)Math.Round(origH * ratio));

        int padW = (target - resizedW) % Stride;
        int padH = (target - resizedH) % Stride;
        int tensorW = resizedW + padW;
        int tensorH = resizedH + padH;

        using var resized = new SKBitmap(
            new SKImageInfo(resizedW, resizedH, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new InvalidOperationException("图像缩放失败（不支持的像素格式）。");

        using var padded = new SKBitmap(
            new SKImageInfo(tensorW, tensorH, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(padded))
        {
            canvas.Clear(new SKColor(114, 114, 114));
            canvas.DrawBitmap(resized, padW / 2, padH / 2);
            canvas.Flush();
        }

        var tensor = new float[3 * tensorH * tensorW];
        int plane = tensorH * tensorW;
        ReadOnlySpan<byte> pixels = padded.GetPixelSpan();
        int rowBytes = padded.RowBytes;
        for (int y = 0; y < tensorH; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < tensorW; x++)
            {
                int p = row + x * 4; // BGRA
                int i = y * tensorW + x;
                tensor[i] = pixels[p] / 255f;               // B — sample feeds BGR
                tensor[plane + i] = pixels[p + 1] / 255f;   // G
                tensor[2 * plane + i] = pixels[p + 2] / 255f; // R
            }
        }

        return new Letterboxed(tensor, tensorW, tensorH, origW, origH);
    }

    /// <summary>
    /// Flattened [rows, cols] predictions → normalized regions: filter conf &gt; 0.25, recover
    /// original-image coordinates with <c>scale_boxes</c> (gain + rounded letterbox padding),
    /// clamp and convert to 0..1 rects. Unknown class names map to "text".
    /// </summary>
    public static List<LayoutRegion> ParseRegions(
        float[] preds,
        int rows,
        int cols,
        Letterboxed geo,
        IReadOnlyDictionary<int, string>? names)
    {
        ArgumentNullException.ThrowIfNull(preds);
        if (rows < 0 || cols < 6 || preds.Length < rows * cols)
            throw new InvalidDataException(
                $"版面模型输出形状异常：{preds.Length} 元素按 {rows}×{cols} 解析。");

        double gain = Math.Min((double)geo.TensorHeight / geo.OriginalHeight,
                               (double)geo.TensorWidth / geo.OriginalWidth);
        double padX = Math.Round((geo.TensorWidth - geo.OriginalWidth * gain) / 2 - 0.1);
        double padY = Math.Round((geo.TensorHeight - geo.OriginalHeight * gain) / 2 - 0.1);

        var regions = new List<LayoutRegion>();
        for (int i = 0; i < rows; i++)
        {
            int b = i * cols;
            float conf = preds[b + 4];
            if (conf <= ConfThreshold) continue;

            double x1 = Math.Clamp((preds[b + 0] - padX) / gain, 0, geo.OriginalWidth);
            double y1 = Math.Clamp((preds[b + 1] - padY) / gain, 0, geo.OriginalHeight);
            double x2 = Math.Clamp((preds[b + 2] - padX) / gain, 0, geo.OriginalWidth);
            double y2 = Math.Clamp((preds[b + 3] - padY) / gain, 0, geo.OriginalHeight);
            if (x2 - x1 < 1 || y2 - y1 < 1) continue;

            int cls = (int)preds[b + 5];
            string kind = names is not null && names.TryGetValue(cls, out string? name)
                ? name
                : "text";

            regions.Add(new LayoutRegion(
                new RectD(x1 / geo.OriginalWidth, y1 / geo.OriginalHeight,
                          (x2 - x1) / geo.OriginalWidth, (y2 - y1) / geo.OriginalHeight),
                kind,
                conf));
        }

        return regions;
    }
}
