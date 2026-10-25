using SkiaSharp;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The naming service now exposes its suggestion step separately so a scan can start naming from
/// page 1. The move step must still never overwrite an existing name.
/// </summary>
public sealed class ContentNamingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-naming-" + Guid.NewGuid().ToString("N"));

    public ContentNamingServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class NoRenderer : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => throw new NotSupportedException();

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private sealed class NoOcr : IOcrEngine
    {
        public string DisplayName => "none";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            throw new NotSupportedException();
    }

    private ContentNamingService Create(AppSettings settings) =>
        new(settings, new NoRenderer(), new NoOcr());

    [Fact]
    public async Task Applying_a_suggestion_moves_the_pdf_and_counts_past_an_existing_name()
    {
        string source = Path.Combine(_dir, "扫描 20260101-120000.pdf");
        File.WriteAllBytes(source, Array.Empty<byte>());
        string occupied = Path.Combine(_dir, "林孝明执照.pdf");
        File.WriteAllBytes(occupied, Array.Empty<byte>());

        var service = Create(new AppSettings { AutoNameByContent = true });
        string moved = await service.ApplyAsync(source, AutoNameResult.Create("林孝明", "执照"));

        Assert.Equal(Path.Combine(_dir, "林孝明执照 (2).pdf"), moved);
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(occupied), "the existing file must never be overwritten");
    }

    /// <summary>A genuinely blank sheet: white, ink-free pixels.</summary>
    private static byte[] WhitePng(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task A_blank_page_is_never_sent_to_ocr()
    {
        var service = Create(new AppSettings { AutoNameByContent = true });

        // NoOcr throws if reached: passing proves the pixel verdict ended the probe before any
        // orientation vote or full-page recognition.
        Assert.Null(await service.TrySuggestFromImageAsync(WhitePng(40, 30)));
    }

    [Fact]
    public async Task Early_naming_does_nothing_when_content_naming_is_off()
    {
        var service = Create(new AppSettings { AutoNameByContent = false });

        Assert.Null(await service.TrySuggestFromImageAsync(new byte[] { 1, 2, 3 }));
    }
}
