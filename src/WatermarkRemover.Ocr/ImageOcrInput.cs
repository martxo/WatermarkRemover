using System.IO;
using SkiaSharp;

namespace WatermarkRemover.Ocr;

/// <summary>
/// Turns a user-supplied photo or scan into the same PNG the PDF renderer hands the OCR engines:
/// flattened onto white, capped on the long side. One input shape means both engines (WeChat
/// takes a PNG file path, Windows OCR decodes the bytes) read images exactly like rendered pages,
/// and a 12 MP phone photo cannot blow past their size limits.
/// </summary>
public static class ImageOcrInput
{
    /// <summary>Same cap the PDF renderer uses, so an image input obeys identical limits.</summary>
    private const int MaxImageSide = 2400;

    /// <summary>Decodes any raster Skia understands into OCR-ready PNG bytes.</summary>
    public static byte[] LoadAsOcrPng(string imagePath)
    {
        using var decoded = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException(
                $"无法读取图片：{Path.GetFileName(imagePath)}（格式不受支持或文件已损坏）。");

        using var flattened = Flatten(decoded);
        int longestSide = Math.Max(flattened.Width, flattened.Height);
        if (longestSide <= MaxImageSide) return EncodePng(flattened);

        using var scaled = Downscale(flattened, MaxImageSide / (double)longestSide);
        return EncodePng(scaled);
    }

    /// <summary>Draws the bitmap onto opaque white — transparent PNGs would OCR poorly.</summary>
    private static SKBitmap Flatten(SKBitmap source)
    {
        var target = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    private static SKBitmap Downscale(SKBitmap source, double factor)
    {
        int width = Math.Max(1, (int)Math.Round(source.Width * factor));
        int height = Math.Max(1, (int)Math.Round(source.Height * factor));
        var target = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell)))
        {
            target.Dispose();
            throw new InvalidOperationException("图片缩放失败。");
        }

        return target;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
