using SkiaSharp;
using WatermarkRemover.App.Scan;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The blank-page cleaner for duplex scans: pixel-judged blankness (no OCR), one in-place
/// multi-page delete, and the keep-at-least-one-page contract even for an all-blank scan.
/// </summary>
public class BlankPageCleanerTests
{
    private static byte[] Page(int width, int height, SkiaSharp.SKColor background, int inkCount = 0)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (int i = 0; i < inkCount; i++)
                canvas.DrawRect(20 + (i % 10) * 24, 20 + (i / 10) * 24, 18, 18, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // --- BlankPageDetector ---

    [Fact]
    public void A_pure_white_page_is_blank()
    {
        byte[] png = Page(600, 840, SKColors.White);
        Assert.True(BlankPageDetector.IsBlank(png));
    }

    [Fact]
    public void Scanner_grey_background_is_still_blank()
    {
        // Real scans never come back pure white; a light grey sheet with no ink is blank.
        byte[] png = Page(600, 840, new SKColor(248, 248, 246));
        Assert.True(BlankPageDetector.IsBlank(png));
    }

    [Fact]
    public void A_page_with_a_handful_of_ink_blocks_is_not_blank()
    {
        byte[] png = Page(600, 840, SKColors.White, inkCount: 12);
        Assert.False(BlankPageDetector.IsBlank(png));
    }

    [Fact]
    public void An_undecodable_image_is_treated_as_content()
    {
        // Deleting a page the detector could not see into would be reckless — fail safe.
        Assert.False(BlankPageDetector.IsBlank([1, 2, 3, 4]));
    }

    // --- PdfPageRotator.DeletePages ---

    /// <summary>A blank multi-page PDF written with PDFsharp itself.</summary>
    private static string NewPdf(int pages)
    {
        string path = Path.Combine(Path.GetTempPath(), "wm-blank-" + Guid.NewGuid().ToString("N") + ".pdf");
        var doc = new PdfSharpCore.Pdf.PdfDocument();
        for (int i = 0; i < pages; i++)
            doc.AddPage();
        doc.Save(path);
        return path;
    }

    private static int PageCount(string pdfPath)
    {
        using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(pdfPath, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }

    [Fact]
    public void DeletePages_removes_exactly_the_named_pages_in_one_save()
    {
        string pdf = NewPdf(5);
        try
        {
            int removed = PdfPageRotator.DeletePages(pdf, [1, 3]);

            Assert.Equal(2, removed);
            Assert.Equal(3, PageCount(pdf));
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public void DeletePages_refuses_to_leave_an_empty_document()
    {
        string pdf = NewPdf(2);
        try
        {
            Assert.Throws<InvalidOperationException>(() => PdfPageRotator.DeletePages(pdf, [0, 1]));
            Assert.Equal(2, PageCount(pdf)); // untouched
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    // --- strip integration: a scan row cleans its own blanks after load ---

    /// <summary>Renders a fixed page image per page index — enough for the cleaner, which only
    /// looks at pixels, never at the file itself.</summary>
    private sealed class PerPageRenderer(IReadOnlyList<byte[]> pages) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pages.Count);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            Task.FromResult(pages[Math.Min(pageIndex, pages.Count - 1)]);

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private static async Task DrainAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && (vm.IsLoading || vm.IsRotating || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task A_scan_row_drops_its_blank_back_sides_after_load()
    {
        string pdf = NewPdf(2); // page 1 has content, page 2 is the duplex feeder's blank back
        try
        {
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf",
                new PerPageRenderer([Page(600, 840, SKColors.White, inkCount: 12), Page(600, 840, SKColors.White)]),
                autoCleanBlank: true);
            await vm.LoadAsync();
            await DrainAsync(vm);

            Assert.Equal(1, vm.PageCount);
            Assert.Equal("已自动删除 1 页空白页", vm.StatusText);
            Assert.Equal(1, PageCount(pdf)); // the file itself lost the blank sheet
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task An_all_blank_scan_keeps_its_first_sheet()
    {
        string pdf = NewPdf(3);
        try
        {
            var blank = Page(600, 840, SKColors.White);
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf",
                new PerPageRenderer([blank, blank, blank]),
                autoCleanBlank: true);
            await vm.LoadAsync();
            await DrainAsync(vm);

            Assert.Equal(1, vm.PageCount); // never allowed to fall to zero
            Assert.Equal("已自动删除 2 页空白页", vm.StatusText);
            Assert.Equal(1, PageCount(pdf));
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
