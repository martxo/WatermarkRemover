using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Pdf.Preview;
using WatermarkRemover.Pdf.Rendering;

namespace WatermarkRemover.Tests;

/// <summary>
/// The delete path must not throw away renders of pages that did not change: only the deleted
/// page's entries go, and everything after it keeps its bytes under its new index.
/// </summary>
public class PdfRenderCacheRemovePageTests
{
    private sealed class StubRenderer(int pageCount) : IPdfPageRenderer
    {
        public List<(int Page, int Dpi)> Calls { get; } = new();

        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
        {
            Calls.Add((pageIndex, dpi));
            // One byte per page index makes a shifted key self-evident (page 5 => 0x05).
            return Task.FromResult(new[] { (byte)(pageIndex + 1) });
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private static string BlankPdf(int pageCount)
    {
        string path = Path.Combine(Path.GetTempPath(), "wm-delcache-" + Guid.NewGuid().ToString("N") + ".pdf");
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pageCount; i++) doc.AddPage();
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task RemovePage_drops_only_the_gap_and_shifts_later_pages_onto_their_new_index()
    {
        string path = BlankPdf(5);
        try
        {
            var renderer = new StubRenderer(5);
            var file = new PdfDocumentFile(path, pathChanged: null, debug: null, info: null);
            var cache = new PdfPageRenderCache(renderer, file);

            for (int page = 0; page < 5; page++)
                await cache.GetPageBytesAsync(page, 96);
            int rendersAfterWarmup = renderer.Calls.Count;
            long bytesAfterWarmup = cache.CacheBytes;

            cache.RemovePage(2); // delete page 3

            // Pages before the gap keep their index and their bytes.
            Assert.Equal(1, (await cache.GetPageBytesAsync(0, 96))[0]);
            Assert.Equal(2, (await cache.GetPageBytesAsync(1, 96))[0]);
            // Pages after it follow their content: old 4 -> new 3, old 5 -> new 4.
            Assert.Equal(4, (await cache.GetPageBytesAsync(2, 96))[0]);
            Assert.Equal(5, (await cache.GetPageBytesAsync(3, 96))[0]);

            // Every read above was a hit: one byte per untouched page, no re-render.
            Assert.Equal(rendersAfterWarmup, renderer.Calls.Count);
            Assert.Equal(4, bytesAfterWarmup - 1); // the deleted page's single byte was released
            Assert.Equal(4, cache.CacheBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemovePage_at_the_head_shifts_every_remaining_page_down_one()
    {
        string path = BlankPdf(3);
        try
        {
            var renderer = new StubRenderer(3);
            var file = new PdfDocumentFile(path, pathChanged: null, debug: null, info: null);
            var cache = new PdfPageRenderCache(renderer, file);

            for (int page = 0; page < 3; page++)
                await cache.GetPageBytesAsync(page, 150);
            int rendersAfterWarmup = renderer.Calls.Count;

            cache.RemovePage(0);

            Assert.Equal(2, (await cache.GetPageBytesAsync(0, 150))[0]);
            Assert.Equal(3, (await cache.GetPageBytesAsync(1, 150))[0]);
            Assert.Equal(rendersAfterWarmup, renderer.Calls.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
