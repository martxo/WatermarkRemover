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

    [Fact]
    public async Task Early_naming_does_nothing_when_content_naming_is_off()
    {
        var service = Create(new AppSettings { AutoNameByContent = false });

        Assert.Null(await service.TrySuggestFromImageAsync(new byte[] { 1, 2, 3 }));
    }
}
