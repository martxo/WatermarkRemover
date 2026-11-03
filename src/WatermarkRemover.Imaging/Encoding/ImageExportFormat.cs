namespace WatermarkRemover.Imaging.Encoding;

/// <summary>
/// 用户可见的图片导出格式。三种格式都由 SkiaSharp 内置的原生编码器产出（libpng /
/// libjpeg-turbo / libwebp）——也就是 Squoosh 那批 wasm 编码器背后的同一批库，
/// 因此不引入任何新的原生依赖或分发体积。
/// </summary>
public enum ImageExportFormat
{
    /// <summary>PNG 无损：兼容性最好（任何看图器/办公软件都能开），体积最大。</summary>
    Png = 0,

    /// <summary>WebP：同画质下体积约为 PNG 的 1/4~1/2（实测文档页 q90 ≈ 27%~37%），
    /// Windows 10 1903+ / 现代办公软件均支持；q100 时无损且仍小于 PNG。</summary>
    Webp = 1,

    /// <summary>JPEG：编码最快（实测约为 PNG 的 1/5 耗时），文字边缘略有振铃；
    /// 适合追求速度、能接受有损的场景。</summary>
    Jpeg = 2,
}

/// <summary>格式的持久化值与 UI 序号换算（设置里存字符串，界面上是下拉框）。</summary>
public static class ImageExportFormats
{
    /// <summary>下拉框顺序：PNG / WebP / JPEG。</summary>
    public static readonly ImageExportFormat[] UiOrder = { ImageExportFormat.Png, ImageExportFormat.Webp, ImageExportFormat.Jpeg };

    /// <summary>设置里存的稳定字符串：png / webp / jpeg。</summary>
    public static string ToSettingsValue(this ImageExportFormat format) => format switch
    {
        ImageExportFormat.Webp => "webp",
        ImageExportFormat.Jpeg => "jpeg",
        _ => "png",
    };

    /// <summary>无法识别的值回退 PNG——历史配置里可能留着别的东西。</summary>
    public static ImageExportFormat ParseOrDefault(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "webp" => ImageExportFormat.Webp,
        "jpeg" or "jpg" => ImageExportFormat.Jpeg,
        _ => ImageExportFormat.Png,
    };

    /// <summary>文件扩展名（含点），与 <see cref="IImageEncoder.FileExtension"/> 一致。</summary>
    public static string FileExtension(this ImageExportFormat format) => format switch
    {
        ImageExportFormat.Webp => ".webp",
        ImageExportFormat.Jpeg => ".jpg",
        _ => ".png",
    };

    public static int IndexFromSettings(string? text)
    {
        int index = Array.IndexOf(UiOrder, ParseOrDefault(text));
        return index < 0 ? 0 : index;
    }

    public static string SettingsValueFromIndex(int index) =>
        UiOrder[Math.Clamp(index, 0, UiOrder.Length - 1)].ToSettingsValue();
}

/// <summary>有损格式的质量档位（1-100）。PNG 无损，忽略质量。</summary>
public static class ImageExportQualities
{
    /// <summary>下拉框顺序：高 / 均衡 / 紧凑。默认「高」= 90（实测文档页 PSNR 47-50dB）。</summary>
    public static readonly int[] Presets = { 90, 80, 70 };

    public static int IndexFromQuality(int quality)
    {
        int index = Array.IndexOf(Presets, Math.Clamp(quality, 1, 100));
        return index < 0 ? 0 : index;
    }

    public static int QualityFromIndex(int index) => Presets[Math.Clamp(index, 0, Presets.Length - 1)];
}
