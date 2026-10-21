using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The strip's gap affordance: every slot — before the first page, between adjacent pages,
/// after the last — sits a hover gap that, on one click, runs the host-supplied scanner and
/// splices the fresh pages into the PDF right there — file, strip and canvas all land on the
/// new arrangement. A refused or failed scan must leave the document exactly as it was.
/// </summary>
public class PdfStripInsertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-strip-insert-" + Guid.NewGuid().ToString("N"));

    public PdfStripInsertTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Never throws; hands back a small plain PNG so decode/render work for real.
    /// Sizes lets a test mix portrait and landscape pages (landscape = a /Rotate-marked spread,
    /// which the real renderer applies before the bitmap ever reaches the strip).</summary>
    private sealed class FakeRenderer(int pageCount, (int W, int H)[]? sizes = null) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
        {
            var (w, h) = sizes is not null && pageIndex < sizes.Length ? sizes[pageIndex] : (30, 40);
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]>(data.ToArray());
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private string WritePdf(string name, params double[] widths)
    {
        string path = Path.Combine(_dir, name);
        using (var doc = new PdfDocument())
        {
            foreach (double width in widths)
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new PdfSharpCore.Drawing.XRect(0, 0, width, 100));
            }
            doc.Save(path);
        }
        return path;
    }

    private static double[] WidthsOf(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages.Cast<PdfPage>().Select(p => p.MediaBox.Width).ToArray();
    }

    private static async Task DrainAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && (vm.IsLoading || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Gaps_bookend_the_strip_and_separate_every_pair()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: _ => Task.FromResult<string?>(null));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            // One atomic [gap | tile | tail] unit per page: the strip's wrap panel can only
            // break between units, so every row — including a wrapped one — opens with a plus
            // slot (a flat gap/tile list used to let the wrap fall between a tile and its next
            // plus, leaving the second row's first tile bare and the columns misaligned).
            // Unit i opens with gap(i-1); only the last unit closes with the trailing gap,
            // because between two adjacent pages one plus serves both edges.
            Assert.Equal(3, vm.StripItems.Count);
            var first = Assert.IsType<PdfPreviewStripUnitViewModel>(vm.StripItems[0]);
            Assert.Equal(-1, first.Gap.InsertAfterIndex);
            Assert.Same(vm.Thumbnails[0], first.Tile);
            Assert.Null(first.Tail);
            var middle = Assert.IsType<PdfPreviewStripUnitViewModel>(vm.StripItems[1]);
            Assert.Equal(0, middle.Gap.InsertAfterIndex);
            Assert.Same(vm.Thumbnails[1], middle.Tile);
            Assert.Null(middle.Tail);
            var last = Assert.IsType<PdfPreviewStripUnitViewModel>(vm.StripItems[2]);
            Assert.Equal(1, last.Gap.InsertAfterIndex);
            Assert.Same(vm.Thumbnails[2], last.Tile);
            Assert.Equal(2, last.Tail!.InsertAfterIndex); // the trailing bookend lives here
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Inserting_scan_pages_splices_the_file_and_rebuilds_the_strip()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        string scanned = WritePdf("scanned.pdf", 210, 220);
        int scans = 0;
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: progress =>
            {
                scans++;
                return Task.FromResult<string?>(scanned);
            });
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(0);

            Assert.Equal(1, scans);
            Assert.Equal(5, vm.PageCount);
            Assert.Equal(new double[] { 100, 210, 220, 110, 120 }, WidthsOf(pdf));
            Assert.Equal(5, vm.StripItems.Count); // one atomic unit per page, rebuilt around the insert
            Assert.Equal(1, vm.PageIndex); // the canvas lands on the first scanned page
            Assert.False(File.Exists(scanned), "the intermediate scan file is removed once spliced in");
            Assert.Contains("已插入 2 页", vm.StatusText);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Inserting_grows_the_strip_in_place_and_keeps_every_existing_tile()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        string scanned = WritePdf("scanned.pdf", 210, 220);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: _ => Task.FromResult<string?>(scanned));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);
            var originals = vm.Thumbnails.ToList();
            var images = originals.Select(t => t.Image).ToList();

            await vm.InsertScanAfterAsync(0);

            // The strip grew in place: the original tiles are the SAME objects with the SAME
            // images — a clear-and-rebuild here is what blanked the strip and repainted it
            // page by page, the jolt the user saw after every insert.
            Assert.Equal(5, vm.Thumbnails.Count);
            Assert.Same(originals[0], vm.Thumbnails[0]);
            Assert.Same(originals[1], vm.Thumbnails[3]);
            Assert.Same(originals[2], vm.Thumbnails[4]);
            Assert.Equal(images[0], vm.Thumbnails[0].Image);
            Assert.Equal(images[1], vm.Thumbnails[3].Image);
            Assert.Equal(images[2], vm.Thumbnails[4].Image);

            // Pages after the gap were re-pointed, not replaced — labels follow their new page.
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, vm.Thumbnails.Select(t => t.PageLabel));
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task A_failed_scan_reports_in_status_and_touches_nothing()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        byte[] before = File.ReadAllBytes(pdf);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: _ => throw new InvalidOperationException("未发现扫描仪。"));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(0);

            Assert.Contains("未发现扫描仪", vm.StatusText);
            Assert.Equal(3, vm.PageCount);
            Assert.Equal(3, vm.StripItems.Count);
            Assert.Equal(before, File.ReadAllBytes(pdf));
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    /// <summary>Scripted orientation oracle: each page probe asks 0° then 180°, and only the
    /// flipped candidate (every odd call) reads as an upright layout — the detector convicts a
    /// 180° turn for every page it is handed, exactly like a stack of upside-down scans.</summary>
    private sealed class UpsideDownScriptedOcr : IBoundedOcrEngine
    {
        public string DisplayName => "UpsideDownScripted";

        private int _call;

        private static IReadOnlyList<RecognizedLine> UprightLines() =>
            Enumerable.Range(0, 8)
                .Select(i => new RecognizedLine($"第{i}行", new RectD(0.1, 0.1 + i * 0.08, 0.8, 0.04)))
                .ToList();

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            RecognizeWithBoundsAsync(imageBytes).ContinueWith(t =>
                (IReadOnlyList<string>)t.Result.Select(l => l.Text).ToList());

        public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
        {
            int turn = _call++ % 2;
            return Task.FromResult(turn == 1 ? UprightLines() : Array.Empty<RecognizedLine>());
        }
    }

    [Fact]
    public async Task Inserted_pages_are_orientation_probed_and_crooked_pages_are_turned()
    {
        // The scripted engine votes "upright layout" on turn 2 for whatever it is handed —
        // the detector convicts a 180° turn, so both inserted pages must end up /Rotate 180
        // while the target's original pages stay untouched.
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        string scanned = WritePdf("scanned.pdf", 210, 220);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            ocrEngine: new UpsideDownScriptedOcr(),
            scanProvider: _ => Task.FromResult<string?>(scanned));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(0);

            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.InformationOnly);
            Assert.Equal(5, doc.Pages.Count);
            Assert.Equal(0, doc.Pages[0].Rotate); // original page untouched
            Assert.Equal(180, doc.Pages[1].Rotate); // inserted page turned
            Assert.Equal(180, doc.Pages[2].Rotate); // inserted page turned
            Assert.Equal(0, doc.Pages[3].Rotate); // original page untouched
            Assert.Equal(new double[] { 100, 210, 220, 110, 120 },
                doc.Pages.Cast<PdfPage>().Select(p => p.MediaBox.Width).ToArray());
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Each_tiles_width_follows_its_own_page_aspect()
    {
        // A strip mixing orientations (landscape scans via /Rotate 270 + an inserted portrait
        // blank) must keep every tile the same height while widths follow each page's true
        // aspect — uniform portrait boxes letterboxed the landscape pages and read as a broken
        // layout: uneven tops, uneven gaps, floating white space.
        string pdf = WritePdf("target.pdf", 100, 100);
        var sizes = new (int W, int H)[] { (60, 40), (30, 40) };
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(2, sizes),
            scanProvider: _ => Task.FromResult<string?>(null));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            var landscape = Assert.IsType<PdfPreviewStripUnitViewModel>(vm.StripItems[0]).Tile;
            var portrait = Assert.IsType<PdfPreviewStripUnitViewModel>(vm.StripItems[1]).Tile;
            Assert.Equal(Math.Round(vm.TileHeight * 60.0 / 40.0), landscape.TileWidth);
            Assert.Equal(Math.Round(vm.TileHeight * 30.0 / 40.0), portrait.TileWidth);
            Assert.True(landscape.TileWidth > portrait.TileWidth);

            vm.ThumbnailScale = 2.0;
            Assert.Equal(Math.Round(vm.TileHeight * 60.0 / 40.0), landscape.TileWidth);
            Assert.Equal(Math.Round(vm.TileHeight * 30.0 / 40.0), portrait.TileWidth);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task A_single_page_document_keeps_both_bookend_gaps_enabled()
    {
        // The user's real case: a one-page scan (a blank sheet, one loose page). Both bookend
        // gaps must be live — the missing page is usually the one before or after it. An
        // earlier "PageCount > 1" guard hid every plus in this document via the template's
        // IsEnabled=False → Hidden trigger, which reads as "no insert feature at all".
        string pdf = WritePdf("target.pdf", 100);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
            scanProvider: _ => Task.FromResult<string?>(null));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            var unit = Assert.Single(vm.StripItems); // one unit carrying both bookends
            var single = Assert.IsType<PdfPreviewStripUnitViewModel>(unit);
            Assert.Equal(-1, single.Gap.InsertAfterIndex);
            Assert.Equal(0, single.Tail!.InsertAfterIndex);
            Assert.True(vm.InsertScanAfterCommand.CanExecute(-1), "front slot of a one-page document");
            Assert.True(vm.InsertScanAfterCommand.CanExecute(0), "back slot of a one-page document");
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task The_leading_gap_prepends_scanned_pages_before_page_one()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        string scanned = WritePdf("scanned.pdf", 210, 220);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: _ => Task.FromResult<string?>(scanned));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(-1);

            Assert.Equal(5, vm.PageCount);
            Assert.Equal(new double[] { 210, 220, 100, 110, 120 }, WidthsOf(pdf));
            Assert.Equal(0, vm.PageIndex); // the canvas lands on the first scanned page
            Assert.False(File.Exists(scanned), "the intermediate scan file is removed once spliced in");
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task The_trailing_gap_appends_scanned_pages_after_the_last_page()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        string scanned = WritePdf("scanned.pdf", 210, 220);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: _ => Task.FromResult<string?>(scanned));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(2); // the last page's index — the strip's trailing gap

            Assert.Equal(5, vm.PageCount);
            Assert.Equal(new double[] { 100, 110, 120, 210, 220 }, WidthsOf(pdf));
            Assert.Equal(3, vm.PageIndex); // the canvas lands on the first scanned page
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    /// <summary>A renderer that behaves like PDFium: /Rotate is applied before the bitmap
    /// exists, so a turned page renders with its DISPLAY size (the box's width and height
    /// swapped for 90/270). A renderer that ignored /Rotate could not tell a turned page from
    /// a straight one — and neither could the strip.</summary>
    private sealed class RotationAwareRenderer : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) =>
            Task.FromResult(DisplaySizes(pdfPath).Count);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
        {
            var (w, h) = DisplaySizes(pdfPath)[pageIndex];
            return Task.FromResult(Png(w, h));
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        /// <summary>Each page's on-screen size in pixels: /Rotate 90/270 swaps the box's sides.</summary>
        private static List<(int W, int H)> DisplaySizes(string pdfPath)
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.InformationOnly);
            return doc.Pages.Cast<PdfPage>().Select(page =>
            {
                double width = page.MediaBox.Width;
                double height = page.MediaBox.Height;
                int rotate = ((page.Rotate % 360) + 360) % 360;
                if (rotate == 90 || rotate == 270) (width, height) = (height, width);
                return ((int)Math.Round(width), (int)Math.Round(height));
            }).ToList();
        }
    }

    /// <summary>Text-volume oracle: the vertical pair reads empty, and only the third call —
    /// the 90° candidate, probed because 0°/180° were ambiguous — reads a page full of text,
    /// so the detector convicts a 90° turn. That turn swaps the page's display width and
    /// height, which is exactly what the strip has to show.</summary>
    private sealed class QuarterTurnScriptedOcr : IOcrEngine
    {
        public string DisplayName => "QuarterTurnScripted";

        private int _call;

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            int turn = _call++ % 4;
            return Task.FromResult<IReadOnlyList<string>>(turn == 2
                ? new[] { "这一页需要顺时针转九十度才能读通顺的文字内容足够多" }
                : Array.Empty<string>());
        }
    }

    private static byte[] Png(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A portrait sheet (A4) — the scanner's own output.</summary>
    private string WritePortraitPdf(string name, int pages = 1)
    {
        string path = Path.Combine(_dir, name);
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < pages; i++)
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new PdfSharpCore.Drawing.XRect(0, 0, 210, 297));
            }
            doc.Save(path);
        }
        return path;
    }

    /// <summary>A landscape sheet (A4 turned a quarter turn) — the credit report's own shape.</summary>
    private string WriteLandscapePdf(string name, int pages = 1)
    {
        string path = Path.Combine(_dir, name);
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < pages; i++)
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new PdfSharpCore.Drawing.XRect(0, 0, 210, 297));
                page.Rotate = 270;
            }
            doc.Save(path);
        }
        return path;
    }

    [Fact]
    public async Task A_turned_inserted_page_is_shown_turned_in_the_strip()
    {
        // The user's case: the inserted page WAS turned in the file (the app log says
        // 「已摆正插入页 … 顺时针 90°」), yet the strip kept showing it as it came out of the
        // scanner. Cause: the probe that measured the page cached its PRE-rotation render, and
        // the tile reads that very cache entry (tile and canvas share one DPI) — the file
        // upright, the picture sideways.
        string pdf = WriteLandscapePdf("target.pdf", 2);
        string scanned = WritePortraitPdf("scanned.pdf");
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new RotationAwareRenderer(),
            ocrEngine: new QuarterTurnScriptedOcr(),
            scanProvider: _ => Task.FromResult<string?>(scanned));
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(-1); // the front slot — the inserted page leads
            await DrainAsync(vm);

            using (var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.InformationOnly))
                Assert.Equal(90, doc.Pages[0].Rotate); // the file really is turned

            var tile = Assert.IsAssignableFrom<BitmapSource>(vm.Thumbnails[0].Image);
            Assert.True(tile.PixelWidth > tile.PixelHeight,
                $"the strip must show the turned page (got {tile.PixelWidth}×{tile.PixelHeight})");

            var canvas = Assert.IsAssignableFrom<BitmapSource>(vm.CurrentImage);
            Assert.True(canvas.PixelWidth > canvas.PixelHeight,
                $"the canvas must show the turned page (got {canvas.PixelWidth}×{canvas.PixelHeight})");
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task An_out_of_range_gap_is_ignored_without_calling_the_scanner()
    {
        string pdf = WritePdf("target.pdf", 100, 110, 120);
        int scans = 0;
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(3),
            scanProvider: _ =>
            {
                scans++;
                return Task.FromResult<string?>(null);
            });
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            await vm.InsertScanAfterAsync(9);
            await vm.InsertScanAfterAsync(-2); // below the leading bookend

            Assert.Equal(0, scans);
            Assert.Equal(3, vm.PageCount);
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
