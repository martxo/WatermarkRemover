using SkiaSharp;

namespace WatermarkRemover.Imaging.Encoding;

/// <summary>一次编码的结果：字节、实际采用的格式与扩展名，以及回退说明（无回退时为 null）。</summary>
public readonly record struct ImageEncodeResult(
    byte[] Bytes, ImageExportFormat Format, string FileExtension, string? Note);

/// <summary>
/// 图片编码/压缩服务（策略注册式）。调用点只说「要什么格式、什么质量」，由服务按格式找到
/// 已注册的编码器；任何格式不可用或编码抛错时，回退到 PNG 并把原因写进
/// <see cref="ImageEncodeResult.Note"/>——不静默失效，也不在调用点堆 if。
/// </summary>
public sealed class ImageEncoderService
{
    private readonly Dictionary<ImageExportFormat, IImageEncoder> _encoders = new();
    private readonly IImageEncoder _fallback;

    /// <summary>默认实例：SkiaSharp 内置的 PNG / WebP / JPEG 三个编码器。</summary>
    public static ImageEncoderService Default { get; } = new();

    public ImageEncoderService(IEnumerable<IImageEncoder>? encoders = null)
    {
        foreach (IImageEncoder encoder in encoders ?? SkiaImageEncoders.Default)
            _encoders[encoder.Format] = encoder;

        if (!_encoders.TryGetValue(ImageExportFormat.Png, out IImageEncoder? fallback))
            throw new ArgumentException("至少必须注册一个 PNG 编码器作为回退", nameof(encoders));

        _fallback = fallback;
    }

    /// <summary>注册（或替换）一个编码器：新编码器只加实现 + 一次注册，调用点零改动。</summary>
    public void Register(IImageEncoder encoder) => _encoders[encoder.Format] = encoder;

    public bool Supports(ImageExportFormat format) => _encoders.ContainsKey(format);

    /// <summary>
    /// 按格式与质量编码。<paramref name="quality"/> 仅对有损格式有效（PNG 与 WebP q100 无损）。
    /// 目标格式不可用或编码失败时回退 PNG，<see cref="ImageEncodeResult.Note"/> 说明原因。
    /// </summary>
    public ImageEncodeResult Encode(SKBitmap bitmap, ImageExportFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (_encoders.TryGetValue(format, out IImageEncoder? encoder))
        {
            try
            {
                return new ImageEncodeResult(
                    encoder.Encode(bitmap, quality), format, encoder.FileExtension, null);
            }
            catch (Exception ex)
            {
                return Fallback(bitmap, $"{Describe(format)}编码失败（{ex.Message}），已按 PNG 保存");
            }
        }

        return Fallback(bitmap, $"当前版本不支持{Describe(format)}格式，已按 PNG 保存");
    }

    private ImageEncodeResult Fallback(SKBitmap bitmap, string note) =>
        new(_fallback.Encode(bitmap, 100), ImageExportFormat.Png, _fallback.FileExtension, note);

    private static string Describe(ImageExportFormat format) => format switch
    {
        ImageExportFormat.Webp => "WebP",
        ImageExportFormat.Jpeg => "JPEG",
        _ => "PNG",
    };
}
