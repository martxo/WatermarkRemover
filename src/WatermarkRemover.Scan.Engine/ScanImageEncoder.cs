using System.Runtime.InteropServices;
using SkiaSharp;
using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// SkiaSharp-backed <see cref="IImageEncoder"/>: encodes a <see cref="RasterImage"/> to
/// JPEG/PNG bytes and downsamples frames. Injected into <c>PdfDocumentBuilder</c> so the
/// PDF layer never references the imaging stack directly.
/// </summary>
public sealed class ScanImageEncoder : IImageEncoder
{
    public IReadOnlyCollection<string> SupportedFormats { get; } = new[] { "png", "jpg", "jpeg", "bmp", "webp" };

    public byte[] Encode(RasterImage image, string format, int quality = 90)
    {
        ArgumentNullException.ThrowIfNull(image);
        var codec = FormatToEncoder(format);
        using var bitmap = ToBitmap(image);
        using var data = bitmap.Encode(codec, quality)
            ?? throw new InvalidOperationException($"SkiaSharp 无法编码为 {format}。");
        return data.ToArray();
    }

    public RasterImage Resize(RasterImage image, int maxWidthOrHeight, bool upscale = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        var longest = Math.Max(image.Width, image.Height);
        if (maxWidthOrHeight <= 0 || (!upscale && longest <= maxWidthOrHeight))
        {
            return image;
        }

        var scale = maxWidthOrHeight / (double)longest;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        using var source = ToBitmap(image);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var target = new SKBitmap(info);
        using var sourcePixels = new SKPixmap(source.Info, source.GetPixels());
        using var targetPixels = new SKPixmap(info, target.GetPixels());
        sourcePixels.ScalePixels(targetPixels, new SKSamplingOptions(SKFilterMode.Linear));

        var data = new byte[info.BytesSize];
        Marshal.Copy(target.GetPixels(), data, 0, data.Length);
        return new RasterImage(width, height, data);
    }

    private static SKEncodedImageFormat FormatToEncoder(string format) => format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
        "bmp" => SKEncodedImageFormat.Bmp,
        "webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Png,
    };

    internal static SKBitmap ToBitmap(RasterImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        Marshal.Copy(image.Bgra, 0, bitmap.GetPixels(), image.Bgra.Length);
        return bitmap;
    }
}
