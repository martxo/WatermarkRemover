using System.Windows;
using System.Windows.Media.Imaging;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 放大看图「底部丢失」排查用的像素采样：读位图最底两行的透明度与亮度。
/// 渲染 PNG → 缓存 → 解码 → 上屏平面，每一环都输出同样的采样——
/// 哪一环第一次出现「透明/偏暗」，丢失就发生在那一环之前。
/// </summary>
internal static class BitmapDiagnostics
{
    /// <summary>描述位图最底两行：透明像素数、偏暗像素数、平均亮度。</summary>
    public static string DescribeBottomRows(BitmapSource bitmap)
    {
        try
        {
            int width = bitmap.PixelWidth;
            int rows = Math.Min(2, bitmap.PixelHeight);
            if (width <= 0 || rows <= 0) return "空位图";

            int stride = width * 4;
            var pixels = new byte[stride * rows];
            bitmap.CopyPixels(new Int32Rect(0, bitmap.PixelHeight - rows, width, rows), pixels, stride, 0);

            int transparent = 0, dark = 0;
            long brightness = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 250) transparent++;
                int b = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                if (b < 64) dark++;
                brightness += b;
            }

            int total = width * rows;
            return $"底{rows}行：透明 {transparent}/{total}，偏暗 {dark}/{total}，平均亮度 {brightness / total}";
        }
        catch (Exception ex)
        {
            return $"采样失败：{ex.Message}";
        }
    }
}
