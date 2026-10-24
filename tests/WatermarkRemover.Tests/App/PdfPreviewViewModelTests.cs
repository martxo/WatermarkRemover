using SkiaSharp;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.App;

public class PdfPreviewViewModelTests
{
    private const string PdfPath = @"C:\tmp\scan.pdf";

    /// <summary>Feeds real PNG bytes so the view model's decode step is exercised, not faked.</summary>
    private static byte[] Png(int red, int green, int blue)
    {
        var info = new SKImageInfo(40, 30, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(new SKColor((byte)red, (byte)green, (byte)blue));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A sized PNG (one diagonal stripe) for thumbnail prescale assertions.</summary>
    private static byte[] Png(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 1 };
            canvas.DrawLine(0, 0, width, height, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class FakeRenderer(int pageCount, bool failRendering = false, int pngWidth = 40, int pngHeight = 30) : IPdfPageRenderer
    {
        /// <summary>How many pages actually went through the renderer — proves the cache works.</summary>
        public int RenderedPageCount { get; private set; }

        /// <summary>Every requested DPI, in call order — lets a test tell render passes apart.</summary>
        public List<int> RenderedDpis { get; } = new();

        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
        {
            if (failRendering) throw new InvalidOperationException("渲染失败");
            RenderedPageCount++;
            RenderedDpis.Add(dpi);
            return Task.FromResult(Png(pngWidth, pngHeight));
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private sealed class UnreadableRenderer : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) =>
            throw new InvalidOperationException("文件损坏");

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    /// <summary>Paging commands fire and forget; a short drain keeps the assertions deterministic.
    /// Also waits out the strip's tile fill: the loop is fire-and-forget too, and a render-count
    /// assertion that runs while a tile is still queued reads one render short under load.</summary>
    private static async Task DrainAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 100 && (vm.IsLoading || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
    }

    /// <summary>The strip fills in tile by tile after the main page is up.</summary>
    private static async Task DrainThumbnailsAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && vm.Thumbnails.Any(thumb => thumb.Image is null); i++)
            await Task.Delay(10);
    }

    /// <summary>Rotation runs fire-and-forget from the tile button; wait for the save to settle.</summary>
    private static async Task DrainRotationAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && vm.IsRotating; i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Load_shows_the_first_page_and_the_total_page_count()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(3));

        await vm.LoadAsync();

        Assert.Equal(3, vm.PageCount);
        Assert.Equal(0, vm.PageIndex);
        Assert.Equal("第 1 / 3 页", vm.PageIndicator);
        Assert.False(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.NotNull(vm.CurrentImage);
        Assert.False(vm.IsLoading);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task Paging_walks_to_the_last_page_and_back()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(3));
        await vm.LoadAsync();
        var firstPage = vm.CurrentImage;

        vm.NextCommand.Execute(null);
        await DrainAsync(vm);

        Assert.Equal(1, vm.PageIndex);
        Assert.Equal("第 2 / 3 页", vm.PageIndicator);
        Assert.True(vm.CanGoPrevious);
        Assert.NotSame(firstPage, vm.CurrentImage);

        vm.NextCommand.Execute(null);
        await DrainAsync(vm);

        Assert.Equal(2, vm.PageIndex);
        Assert.False(vm.CanGoNext);

        vm.PreviousCommand.Execute(null);
        await DrainAsync(vm);

        Assert.Equal(1, vm.PageIndex);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public async Task Trimming_the_cache_makes_the_next_visit_render_again()
    {
        var renderer = new FakeRenderer(3);
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", renderer);
        await vm.LoadAsync();
        await DrainAsync(vm);
        vm.NextCommand.Execute(null); // page 1, cached so going back can hit it
        await DrainAsync(vm);

        // Folded strips give their full-page PNGs back; the row keeps its tiles, so a reopen
        // must re-render on demand rather than show a blank page.
        vm.TrimRenderCache();
        int before = renderer.RenderedPageCount;

        vm.PreviousCommand.Execute(null); // page 0 again — was cached, now must miss
        await DrainAsync(vm);

        Assert.Equal(0, vm.PageIndex);
        Assert.True(renderer.RenderedPageCount > before);
        Assert.NotNull(vm.CurrentImage);
    }

    [Fact]
    public async Task Revisiting_a_page_does_not_render_it_again()
    {
        var renderer = new FakeRenderer(3);
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", renderer);
        await vm.LoadAsync();

        vm.NextCommand.Execute(null);
        await DrainAsync(vm);
        vm.PreviousCommand.Execute(null);
        await DrainAsync(vm);
        vm.NextCommand.Execute(null);
        await DrainAsync(vm);

        // Two canvas visits (pages 0 and 1) render once each; page 1 is revisited but served
        // from cache. The strip renders its own 96-dpi tiles for all three pages.
        Assert.Equal(2, renderer.RenderedDpis.Count(dpi => dpi == 150));
        Assert.Equal(3, renderer.RenderedDpis.Count(dpi => dpi == 96));
        Assert.Equal(1, vm.PageIndex);
    }

    [Fact]
    public async Task Thumbnails_are_prescaled_to_a_modest_oversample()
    {
        var renderer = new FakeRenderer(1, pngWidth: 1200, pngHeight: 1600);
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", renderer);
        await vm.LoadAsync();
        await DrainThumbnailsAsync(vm);

        // The tile would be fine with ~120 px; a 1600 px bitmap handed to WPF makes it do a
        // single ~13x downscale that eats thin lines. The strip must pre-scale instead — to
        // 480 long side, still enough oversample for the grid's 4.0 zoom ceiling.
        var thumb = Assert.IsAssignableFrom<BitmapSource>(vm.Thumbnails[0].Image);
        Assert.Equal(360, thumb.PixelWidth);
        Assert.Equal(480, thumb.PixelHeight);
    }

    [Fact]
    public async Task A_render_failure_shows_readable_text_instead_of_throwing()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(3, failRendering: true));

        await vm.LoadAsync();

        Assert.Null(vm.CurrentImage);
        Assert.StartsWith("预览失败", vm.StatusText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task An_unreadable_document_reports_the_reason()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new UnreadableRenderer());

        await vm.LoadAsync();

        Assert.Equal("无法打开这份 PDF：文件损坏", vm.StatusText);
        Assert.Null(vm.CurrentImage);
    }

    [Fact]
    public async Task An_empty_document_reports_no_pages_and_renders_nothing()
    {
        var renderer = new FakeRenderer(0);
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", renderer);

        await vm.LoadAsync();

        Assert.Equal("这份 PDF 没有可显示的页面。", vm.StatusText);
        Assert.Equal("无页面", vm.PageIndicator);
        Assert.Null(vm.CurrentImage);
        Assert.Equal(0, renderer.RenderedPageCount);
        Assert.False(vm.HasThumbnails);
    }

    [Fact]
    public async Task A_cancelled_session_renders_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var renderer = new FakeRenderer(3);
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", renderer, cts.Token);

        await vm.LoadAsync();

        Assert.Equal(0, vm.PageCount);
        Assert.Null(vm.CurrentImage);
        Assert.Equal(0, renderer.RenderedPageCount);
    }

    [Fact]
    public void Close_command_asks_the_host_to_leave_the_overlay()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(1));
        bool closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closeRequested);
    }

    [Fact]
    public async Task Loading_builds_a_tile_per_page_and_marks_the_shown_page()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(3));

        await vm.LoadAsync();
        await DrainThumbnailsAsync(vm);

        Assert.True(vm.HasThumbnails);
        Assert.Equal(3, vm.Thumbnails.Count);
        Assert.All(vm.Thumbnails, thumb => Assert.NotNull(thumb.Image));
        Assert.True(vm.Thumbnails[0].IsSelected);
        Assert.False(vm.Thumbnails[1].IsSelected);
        Assert.Equal("1", vm.Thumbnails[0].PageLabel);
        Assert.Equal("3", vm.Thumbnails[2].PageLabel);
    }

    [Fact]
    public async Task Clicking_a_tile_enlarges_that_page_in_the_canvas()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(3));
        await vm.LoadAsync();
        await DrainThumbnailsAsync(vm);

        vm.Thumbnails[2].SelectCommand.Execute(null);
        await DrainAsync(vm);

        Assert.Equal(2, vm.PageIndex);
        Assert.Equal("第 3 / 3 页", vm.PageIndicator);
        Assert.True(vm.Thumbnails[2].IsSelected);
        Assert.False(vm.Thumbnails[0].IsSelected);
    }

    [Fact]
    public void Zoom_rail_steps_the_tile_size_and_clamps_at_both_ends()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(1));

        Assert.Equal(92, vm.TileWidth);
        Assert.Equal(118, vm.TileHeight);

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(106, vm.TileWidth); // 92 × 1.15 = 105.8, rounded

        for (int i = 0; i < 20; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(368, vm.TileWidth); // capped at 4.0
        Assert.False(vm.ZoomInCommand.CanExecute(null));

        for (int i = 0; i < 30; i++) vm.ZoomOutCommand.Execute(null);
        Assert.Equal(55, vm.TileWidth); // floored at 0.6
        Assert.False(vm.ZoomOutCommand.CanExecute(null));

        vm.ThumbnailScale = 99; // direct set clamps too
        Assert.Equal(368, vm.TileWidth);
    }

    [Fact]
    public void The_inline_grid_is_capped_at_two_tile_rows_so_long_documents_scroll_inside()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(1));

        Assert.Equal(118 * PdfPreviewViewModel.ThumbAreaRows, vm.ThumbAreaMaxHeight);

        vm.ThumbnailScale = 2.0;
        Assert.Equal(Math.Round(118 * 2.0) * PdfPreviewViewModel.ThumbAreaRows, vm.ThumbAreaMaxHeight);
    }

    [Fact]
    public async Task A_tile_click_requests_the_screen_level_viewer()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(2));
        await vm.LoadAsync();
        int requests = 0;
        vm.EnlargeRequested += (_, _) => requests++;

        // The image-browser interaction: a tile click renders that page AND asks the host to
        // open the screen-level viewer on it.
        vm.Thumbnails[1].SelectCommand.Execute(null);
        await DrainAsync(vm);
        Assert.Equal(1, requests);
        Assert.Equal(1, vm.PageIndex);
    }

    [Fact]
    public async Task Rotating_a_tile_saves_the_pdf_and_refreshes_both_views()
    {
        string path = Path.Combine(Path.GetTempPath(), "wm-prevrotate-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.CamScannerLike().Build());
        try
        {
            var renderer = new FakeRenderer(1);
            var vm = new PdfPreviewViewModel(path, "doc.pdf", renderer);
            await vm.LoadAsync();
            await DrainThumbnailsAsync(vm);
            int rendersBefore = renderer.RenderedPageCount;

            vm.Thumbnails[0].RotateCommand.Execute(null);
            await DrainRotationAsync(vm);

            Assert.Contains("/Rotate 90", File.ReadAllText(path));
            Assert.False(vm.IsRotating);
            Assert.True(vm.Thumbnails[0].RotateCommand.CanExecute(null)); // lock released
            // The rotation is saved silently otherwise; the strip now says what just happened.
            Assert.Equal("第 1 页已旋转，已保存", vm.StatusText);
            // The tile and the canvas both re-rendered the rotated page instead of showing a stale image.
            Assert.True(renderer.RenderedPageCount > rendersBefore);
            Assert.NotNull(vm.Thumbnails[0].Image);
            Assert.NotNull(vm.CurrentImage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_rotation_that_cannot_be_saved_reports_the_reason_without_sticking()
    {
        var vm = new PdfPreviewViewModel(PdfPath, "scan.pdf", new FakeRenderer(2));
        await vm.LoadAsync();
        await DrainThumbnailsAsync(vm);

        vm.Thumbnails[0].RotateCommand.Execute(null);
        await DrainRotationAsync(vm);

        // The path never existed, so the rename tracker can't heal it — the user gets the
        // dedicated "moved or renamed" line instead of a raw path error.
        Assert.StartsWith("文件已被移动或重命名", vm.StatusText);
        Assert.False(vm.IsRotating);
        Assert.True(vm.Thumbnails[0].RotateCommand.CanExecute(null)); // lock released
        Assert.NotNull(vm.CurrentImage); // the page on screen is untouched
    }

    // --- page delete ---

    /// <summary>A blank multi-page PDF written with PDFsharp itself.</summary>
    private static string WriteMultiPagePdf(int pageCount)
    {
        string path = Path.Combine(Path.GetTempPath(), "wm-prevdel-" + Guid.NewGuid().ToString("N") + ".pdf");
        using (var doc = new PdfSharpCore.Pdf.PdfDocument())
        {
            for (int i = 0; i < pageCount; i++) doc.AddPage();
            doc.Save(path);
        }
        return path;
    }

    private static int ReadPageCount(string path)
    {
        using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }

    [Fact]
    public async Task Deleting_a_page_saves_the_pdf_and_rebuilds_the_strip()
    {
        string path = WriteMultiPagePdf(3);
        try
        {
            var vm = new PdfPreviewViewModel(path, "doc.pdf", new FakeRenderer(3));
            await vm.LoadAsync();
            await DrainThumbnailsAsync(vm);

            vm.Thumbnails[1].DeleteCommand.Execute(null);
            await DrainRotationAsync(vm);

            Assert.Equal(2, ReadPageCount(path)); // the file on disk really lost page 2
            Assert.Equal(2, vm.PageCount);
            Assert.Equal(2, vm.Thumbnails.Count);
            Assert.Equal("2", vm.Thumbnails[1].PageLabel);
            Assert.Equal(1, vm.PageIndex); // the page that took its place is now shown
            Assert.True(vm.Thumbnails[1].IsSelected);
            Assert.Equal("第 2 / 2 页", vm.PageIndicator);
            Assert.False(vm.IsRotating);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_declined_delete_touches_neither_the_file_nor_the_strip()
    {
        string path = WriteMultiPagePdf(2);
        try
        {
            int? askedFor = null;
            var vm = new PdfPreviewViewModel(path, "doc.pdf", new FakeRenderer(2),
                confirmPageDelete: index => { askedFor = index; return false; });
            await vm.LoadAsync();
            await DrainThumbnailsAsync(vm);

            vm.Thumbnails[0].DeleteCommand.Execute(null);
            await DrainRotationAsync(vm);

            Assert.Equal(0, askedFor);
            Assert.Equal(2, ReadPageCount(path));
            Assert.Equal(2, vm.PageCount);
            Assert.Equal(2, vm.Thumbnails.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Deleting_the_last_page_is_refused_and_the_tile_stays_disabled()
    {
        string path = WriteMultiPagePdf(1);
        try
        {
            var vm = new PdfPreviewViewModel(path, "doc.pdf", new FakeRenderer(1));
            await vm.LoadAsync();
            bool closeRequested = false;
            vm.RequestClose += (_, _) => closeRequested = true;

            Assert.False(vm.Thumbnails[0].DeleteCommand.CanExecute(null));

            // Even a bypass of the disabled button must not eat the only page.
            vm.Thumbnails[0].DeleteCommand.Execute(null);
            await vm.DeletePageAsync(0);
            await DrainRotationAsync(vm);

            Assert.Equal(1, ReadPageCount(path));
            Assert.Equal(1, vm.PageCount);
            Assert.True(vm.HasThumbnails);
            Assert.NotNull(vm.CurrentImage);
            Assert.False(closeRequested);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
