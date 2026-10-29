using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Pdf.Preview;

namespace WatermarkRemover.Tests.Pdf;

/// <summary>
/// The per-document 48 MB cap is not enough on its own: several open strips could hold several
/// times that and the app's managed heap reached multi-GB. The process-wide budget must make other
/// documents hand their bytes back while the page being read keeps its own.
/// </summary>
public sealed class RenderCacheBudgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-budget-" + Guid.NewGuid().ToString("N"));

    public RenderCacheBudgetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Returns a PNG-sized payload of an exact length; never reads the file.</summary>
    private sealed class SizedRenderer : IPdfPageRenderer
    {
        private readonly int _bytes;

        public SizedRenderer(int bytes) => _bytes = bytes;

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            Task.FromResult(new byte[_bytes]);

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            Task.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            Task.FromResult(new byte[_bytes]);

        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(1);
    }

    private PdfDocumentFile NewFile(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "dummy pdf");
        return new PdfDocumentFile(path);
    }

    [Fact]
    public async Task Exceeding_the_process_budget_releases_the_other_documents_cache()
    {
        long original = PdfPageRenderCache.ProcessBudgetBytes;
        int payload = 3 * 1024 * 1024;
        PdfPageRenderCache.ProcessBudgetBytes = 4 * 1024 * 1024; // room for one document only
        try
        {
            var first = new PdfPageRenderCache(new SizedRenderer(payload), NewFile("first.pdf"));
            var second = new PdfPageRenderCache(new SizedRenderer(payload), NewFile("second.pdf"));

            await first.GetPageBytesAsync(0, 96);
            Assert.Equal(payload, first.CacheBytes);

            // Second document pushes the total past the process budget: the first one lets go.
            await second.GetPageBytesAsync(0, 96);

            Assert.Equal(payload, second.CacheBytes);
            Assert.Equal(0, first.CacheBytes);
        }
        finally
        {
            PdfPageRenderCache.ProcessBudgetBytes = original;
        }
    }

    [Fact]
    public async Task Staying_under_the_budget_keeps_every_document_resident()
    {
        long original = PdfPageRenderCache.ProcessBudgetBytes;
        int payload = 1024 * 1024;
        PdfPageRenderCache.ProcessBudgetBytes = 8 * 1024 * 1024;
        try
        {
            var first = new PdfPageRenderCache(new SizedRenderer(payload), NewFile("a.pdf"));
            var second = new PdfPageRenderCache(new SizedRenderer(payload), NewFile("b.pdf"));

            await first.GetPageBytesAsync(0, 96);
            await second.GetPageBytesAsync(0, 96);

            Assert.Equal(payload, first.CacheBytes);
            Assert.Equal(payload, second.CacheBytes);
        }
        finally
        {
            PdfPageRenderCache.ProcessBudgetBytes = original;
        }
    }
}
