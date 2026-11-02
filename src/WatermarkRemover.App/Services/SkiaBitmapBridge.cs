using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WatermarkRemover.App.Logging;
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

    /// <summary>
    /// WPF 位图 → Skia 位图（看图器标注的合成底图）。走「PNG 编码 → Skia 解码」的保真回路：
    /// 与标注窗直接 <see cref="SKBitmap.Decode(string)"/> 出来的字节行为一致（alpha 直通），
    /// 且不挑 WPF 像素格式（Bgr32/Bgra32/Pbgra32/TransformedBitmap 都能过）。编码失败
    /// （极端格式）时返回 null，宿主据此提示「暂不支持标注」，不猜格式硬转。
    /// </summary>
    public static SKBitmap? FromBitmapSource(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            return SKBitmap.Decode(stream);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"位图转 Skia 失败：{ex.Message}");
            return null;
        }
    }
}
