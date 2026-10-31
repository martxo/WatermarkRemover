using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using PixelFormat = System.Windows.Media.PixelFormat;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Converts a Skia bitmap into a frozen WPF bitmap. Screenshots are always fully opaque
/// (the desktop capture is opaque and every annotation is composited over it), so the straight
/// BGRA pixels can be handed to WPF as-is.
/// </summary>
internal static class SkiaBitmapBridge
{
    /// <summary>Copies a physical-pixel slice out of a bitmap; null when the rect is outside it.</summary>
    public static SKBitmap? Crop(SKBitmap source, SKRectI subset)
    {
        var clamped = new SKRectI(
            Math.Max(subset.Left, 0),
            Math.Max(subset.Top, 0),
            Math.Min(subset.Right, source.Width),
            Math.Min(subset.Bottom, source.Height));
        if (clamped.Width <= 0 || clamped.Height <= 0) return null;

        var target = new SKBitmap(new SKImageInfo(
            clamped.Width, clamped.Height, source.ColorType, source.AlphaType));
        if (source.ExtractSubset(target, clamped)) return target;

        target.Dispose();
        return null;
    }

    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // Screen captures carry alpha 0 in every pixel (GDI); an opaque bitmap is therefore
        // wrapped as Bgr32 so WPF never treats it as fully transparent. Composed previews are
        // premultiplied with alpha 255 over the opaque background and use Bgra32.
        PixelFormat format = bitmap.AlphaType == SKAlphaType.Opaque
            ? PixelFormats.Bgr32
            : PixelFormats.Bgra32;

        var source = BitmapSource.Create(
            bitmap.Width, bitmap.Height, 96, 96,
            format, null, bitmap.Bytes.ToArray(), bitmap.RowBytes);
        source.Freeze();
        return source;
    }
}
