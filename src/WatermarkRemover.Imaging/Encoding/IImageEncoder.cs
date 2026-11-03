using SkiaSharp;

namespace WatermarkRemover.Imaging.Encoding;

/// <summary>
/// 一种图片格式的编码器。注册进 <see cref="ImageEncoderService"/> 即可被调用点使用——
/// 新增编码器（比如以后接 MozJPEG / oxipng 原生二进制）只需要实现本接口并注册，
/// 调用点不需要加任何 if 分支。
/// </summary>
public interface IImageEncoder
{
    ImageExportFormat Format { get; }

    /// <summary>该编码器产出的文件扩展名（含点）。</summary>
    string FileExtension { get; }

    /// <summary>把位图编码成字节；失败应抛异常，由 <see cref="ImageEncoderService"/> 负责回退。</summary>
    byte[] Encode(SKBitmap bitmap, int quality);
}

/// <summary>SkiaSharp 内置编码器（libpng / libjpeg-turbo / libwebp，随 SkiaSharp 原生包分发）。</summary>
public sealed class SkiaImageEncoder : IImageEncoder
{
    private readonly SKEncodedImageFormat _format;

    public SkiaImageEncoder(ImageExportFormat format, string fileExtension)
    {
        Format = format;
        FileExtension = fileExtension;
        _format = format switch
        {
            ImageExportFormat.Webp => SKEncodedImageFormat.Webp,
            ImageExportFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            _ => SKEncodedImageFormat.Png,
        };
    }

    public ImageExportFormat Format { get; }

    public string FileExtension { get; }

    public byte[] Encode(SKBitmap bitmap, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(_format, Math.Clamp(quality, 1, 100));
        return data?.ToArray() ?? throw new InvalidOperationException($"{Format} 编码失败（编码器未产出数据）");
    }
}

/// <summary>默认注册的三个 SkiaSharp 编码器。</summary>
public static class SkiaImageEncoders
{
    public static IEnumerable<IImageEncoder> Default => new IImageEncoder[]
    {
        new SkiaImageEncoder(ImageExportFormat.Png, ".png"),
        new SkiaImageEncoder(ImageExportFormat.Webp, ".webp"),
        new SkiaImageEncoder(ImageExportFormat.Jpeg, ".jpg"),
    };
}
