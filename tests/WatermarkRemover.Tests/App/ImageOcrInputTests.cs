using SkiaSharp;
using WatermarkRemover.Ocr;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Image inputs must arrive at the OCR engines in the exact shape rendered pages do: PNG bytes,
/// white background, capped on the long side. These pin the loader itself — the feature tests
/// inject a fake loader and would not notice a broken one.
/// </summary>
public class ImageOcrInputTests
{
    private static string WritePng(string directory, int width, int height, bool transparent = false)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(transparent ? SKColors.Transparent : SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        string path = Path.Combine(directory, $"img-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void A_small_image_comes_back_as_png_at_its_own_size()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wm-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = WritePng(dir, 40, 30);

            byte[] png = ImageOcrInput.LoadAsOcrPng(input);

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
            using var decoded = SKBitmap.Decode(png);
            Assert.Equal(40, decoded.Width);
            Assert.Equal(30, decoded.Height);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_large_image_is_capped_on_the_long_side_like_a_rendered_page()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wm-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = WritePng(dir, 3000, 600);

            byte[] png = ImageOcrInput.LoadAsOcrPng(input);

            using var decoded = SKBitmap.Decode(png);
            Assert.Equal(2400, Math.Max(decoded.Width, decoded.Height));
            Assert.Equal(480, decoded.Height);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void An_unreadable_file_reports_a_readable_reason()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wm-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "broken.jpg");
            File.WriteAllBytes(input, new byte[] { 0xFF, 0xD8, 0x00 });

            var ex = Assert.Throws<InvalidOperationException>(() => ImageOcrInput.LoadAsOcrPng(input));

            Assert.Contains("无法读取图片", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
