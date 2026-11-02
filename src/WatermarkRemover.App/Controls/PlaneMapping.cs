using System.Windows;

namespace WatermarkRemover.App.Controls;

/// <summary>
/// 图片平面（<see cref="ImageSurface"/> 里按「旋转后尺寸 × 缩放」布局的那块）与位图像素
/// 之间的换算。纯函数、不依赖 WPF 实例，便于单测；截图查看器与 PDF 页宿主共用。
/// 约定：平面坐标 = 相对图片左上角的 DIP；位图坐标 = 原图（未旋转）像素。
/// 旋转反算的依据：平面显示的是预旋转后的位图，90° 顺时针下原图 (x,y) 落在
/// (H-1-y, x)，270° 落在 (y, W-1-x)，180° 落在 (W-1-x, H-1-y)。
/// </summary>
internal static class PlaneMapping
{
    /// <summary>平面坐标（DIP）→ 位图坐标（原图像素）。</summary>
    public static Point ToPixel(double angle, int pixelWidth, int pixelHeight, double scale, Point plane)
    {
        if (scale <= 0) return default;

        double rx = plane.X / scale;
        double ry = plane.Y / scale;
        return Quarter(angle) switch
        {
            90 => new Point(ry, pixelHeight - 1 - rx),
            180 => new Point(pixelWidth - 1 - rx, pixelHeight - 1 - ry),
            270 => new Point(pixelWidth - 1 - ry, rx),
            _ => new Point(rx, ry),
        };
    }

    /// <summary>位图坐标（原图像素）→ 平面坐标（DIP，相对图片左上角）。</summary>
    public static Point ToPlane(double angle, int pixelWidth, int pixelHeight, double scale, Point pixel)
    {
        (double rx, double ry) = Quarter(angle) switch
        {
            90 => (pixelHeight - 1 - pixel.Y, pixel.X),
            180 => (pixelWidth - 1 - pixel.X, pixelHeight - 1 - pixel.Y),
            270 => (pixel.Y, pixelWidth - 1 - pixel.X),
            _ => (pixel.X, pixel.Y),
        };
        return new Point(rx * scale, ry * scale);
    }

    private static int Quarter(double angle)
    {
        int quarter = (int)Math.Round(angle) % 360;
        return quarter < 0 ? quarter + 360 : quarter;
    }
}
