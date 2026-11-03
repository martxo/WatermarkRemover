using SkiaSharp;
using WatermarkRemover.Imaging.Encoding;

namespace WatermarkRemover.Tests.Imaging;

/// <summary>
/// 注册式编码服务的契约：按格式找编码器、失败回退 PNG 并说明、扩展名与格式一致。
/// 这三个性质是「截图/标注导出不静默换格式」的底线，逐个钉死。
/// </summary>
public sealed class ImageEncoderServiceTests
{
    private static SKBitmap Sample()
    {
        var bitmap = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(30, 90, 200));
        canvas.DrawRect(new SKRect(8, 8, 40, 32), new SKPaint { Color = new SKColor(240, 240, 240) });
        canvas.Flush();
        return bitmap;
    }

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png")]
    [InlineData(ImageExportFormat.Webp, ".webp")]
    [InlineData(ImageExportFormat.Jpeg, ".jpg")]
    public void Every_default_format_encodes_with_its_own_extension(ImageExportFormat format, string extension)
    {
        using var bitmap = Sample();
        ImageEncodeResult result = ImageEncoderService.Default.Encode(bitmap, format, 90);

        Assert.Equal(format, result.Format);
        Assert.Equal(extension, result.FileExtension);
        Assert.Null(result.Note);
        Assert.True(result.Bytes.Length > 0);

        // 产出的字节必须能原样解回：编码格式不是摆设。
        using var decoded = SKBitmap.Decode(result.Bytes);
        Assert.NotNull(decoded);
        Assert.Equal(64, decoded!.Width);
        Assert.Equal(48, decoded.Height);
    }

    [Fact]
    public void Png_is_bit_exact_and_webp_lossless_is_smaller()
    {
        using var bitmap = Sample();

        ImageEncodeResult png = ImageEncoderService.Default.Encode(bitmap, ImageExportFormat.Png, 100);
        using var pngDecoded = SKBitmap.Decode(png.Bytes);
        Assert.Equal(bitmap.GetPixel(20, 20), pngDecoded!.GetPixel(20, 20));

        // WebP q100 在 Skia 的 libwebp 下走无损：同样逐像素一致，但体积小于 PNG
        // （实测文档页约为 PNG 的 39%）。
        ImageEncodeResult webp = ImageEncoderService.Default.Encode(bitmap, ImageExportFormat.Webp, 100);
        Assert.True(webp.Bytes.Length < png.Bytes.Length,
            $"WebP 无损应小于 PNG：webp={webp.Bytes.Length} png={png.Bytes.Length}");
        using var webpDecoded = SKBitmap.Decode(webp.Bytes);
        Assert.Equal(bitmap.GetPixel(20, 20), webpDecoded!.GetPixel(20, 20));
    }

    [Fact]
    public void A_failing_encoder_falls_back_to_png_and_says_why()
    {
        var service = new ImageEncoderService(new IImageEncoder[]
        {
            new SkiaImageEncoder(ImageExportFormat.Png, ".png"),
            new ExplodingEncoder(ImageExportFormat.Webp, ".webp"),
        });

        using var bitmap = Sample();
        ImageEncodeResult result = service.Encode(bitmap, ImageExportFormat.Webp, 90);

        Assert.Equal(ImageExportFormat.Png, result.Format);   // 回退到 PNG
        Assert.Equal(".png", result.FileExtension);           // 扩展名跟着回退
        Assert.NotNull(result.Note);                          // 原因必须说明，不静默
        Assert.Contains("WebP", result.Note!);
        using var decoded = SKBitmap.Decode(result.Bytes);
        Assert.NotNull(decoded);
    }

    [Fact]
    public void An_unregistered_format_falls_back_to_png_and_says_why()
    {
        var service = new ImageEncoderService(new IImageEncoder[]
        {
            new SkiaImageEncoder(ImageExportFormat.Png, ".png"),
        });

        using var bitmap = Sample();
        ImageEncodeResult result = service.Encode(bitmap, ImageExportFormat.Jpeg, 90);

        Assert.Equal(ImageExportFormat.Png, result.Format);
        Assert.Contains("JPEG", result.Note!);
    }

    [Fact]
    public void A_newly_registered_encoder_is_used_without_touching_callers()
    {
        var service = new ImageEncoderService(new IImageEncoder[]
        {
            new SkiaImageEncoder(ImageExportFormat.Png, ".png"),
        });
        service.Register(new SkiaImageEncoder(ImageExportFormat.Jpeg, ".jpg"));

        using var bitmap = Sample();
        ImageEncodeResult result = service.Encode(bitmap, ImageExportFormat.Jpeg, 90);

        Assert.Equal(ImageExportFormat.Jpeg, result.Format);
        Assert.Equal(".jpg", result.FileExtension);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Quality_is_clamped_into_the_encoder_range()
    {
        using var bitmap = Sample();
        // 0 与 500 都不应让编码器抛异常（内部 Clamp 到 1..100）。
        Assert.True(ImageEncoderService.Default.Encode(bitmap, ImageExportFormat.Webp, 0).Bytes.Length > 0);
        Assert.True(ImageEncoderService.Default.Encode(bitmap, ImageExportFormat.Webp, 500).Bytes.Length > 0);
    }

    [Theory]
    [InlineData("png", ImageExportFormat.Png)]
    [InlineData("webp", ImageExportFormat.Webp)]
    [InlineData("jpeg", ImageExportFormat.Jpeg)]
    [InlineData("jpg", ImageExportFormat.Jpeg)]
    [InlineData("WEBP", ImageExportFormat.Webp)]
    [InlineData("", ImageExportFormat.Png)]
    [InlineData("avif", ImageExportFormat.Png)]   // 未知值回退 PNG，不抛
    [InlineData(null, ImageExportFormat.Png)]
    public void Settings_text_maps_to_a_format(string? text, ImageExportFormat expected)
    {
        Assert.Equal(expected, ImageExportFormats.ParseOrDefault(text));
    }

    [Theory]
    [InlineData(0, "png")]
    [InlineData(1, "webp")]
    [InlineData(2, "jpeg")]
    [InlineData(9, "jpeg")]  // 越界序号夹到端点
    public void Combo_box_index_round_trips(int index, string settingsValue)
    {
        Assert.Equal(settingsValue, ImageExportFormats.SettingsValueFromIndex(index));
        Assert.Equal(Math.Clamp(index, 0, 2), ImageExportFormats.IndexFromSettings(settingsValue));
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(1, 80)]
    [InlineData(2, 70)]
    [InlineData(7, 70)]      // 越界序号夹到端点
    public void Quality_preset_index_round_trips(int index, int quality)
    {
        Assert.Equal(quality, ImageExportQualities.QualityFromIndex(index));
        Assert.Equal(Math.Clamp(index, 0, 2), ImageExportQualities.IndexFromQuality(quality));
    }

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png")]
    [InlineData(ImageExportFormat.Webp, ".webp")]
    [InlineData(ImageExportFormat.Jpeg, ".jpg")]
    public void Extension_agrees_with_the_encoder(ImageExportFormat format, string extension)
    {
        Assert.Equal(extension, format.FileExtension());
        Assert.Equal(format.ToSettingsValue(), ImageExportFormats.ParseOrDefault(format.ToSettingsValue()).ToSettingsValue());
    }

    private sealed class ExplodingEncoder : IImageEncoder
    {
        public ExplodingEncoder(ImageExportFormat format, string fileExtension)
        {
            Format = format;
            FileExtension = fileExtension;
        }

        public ImageExportFormat Format { get; }

        public string FileExtension { get; }

        public byte[] Encode(SKBitmap bitmap, int quality) =>
            throw new InvalidOperationException("boom（模拟编码器不可用）");
    }
}
