using System.Runtime.InteropServices;
using WatermarkRemover.Scan.Core.Models;
using SkiaSharp;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Bridges SkiaSharp bitmaps into the core <see cref="RasterImage"/> (32-bit BGRA, top-left origin,
/// no row padding). SkiaSharp is a pure imaging library, so it stays outside the UI stack.
/// </summary>
internal static class SkiaRaster
{
    /// <summary>Decodes encoded image bytes (PNG from WIA, or any other supported format).</summary>
    public static RasterImage Decode(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded)
            ?? throw new InvalidOperationException("SkiaSharp 无法解码该图像数据。");
        return FromBitmap(bitmap);
    }

    /// <summary>Decodes an image file from disk.</summary>
    public static RasterImage DecodeFile(string path)
    {
        using var bitmap = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException($"SkiaSharp 无法解码图像文件：{path}");
        return FromBitmap(bitmap);
    }

    private static RasterImage FromBitmap(SKBitmap source)
    {
        // Copy shuffles the channels into BGRA8888; the row bytes may still carry Skia's own padding.
        using var converted = source.Copy(SKColorType.Bgra8888);
        var info = converted.Info;
        var width = info.Width;
        var height = info.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var sourcePtr = converted.GetPixels();
        if (sourcePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("SkiaSharp 位图没有像素数据。");
        }

        if (info.RowBytes == stride)
        {
            Marshal.Copy(sourcePtr, pixels, 0, pixels.Length);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(sourcePtr + (y * info.RowBytes), pixels, y * stride, stride);
            }
        }

        return new RasterImage(width, height, pixels);
    }
}
