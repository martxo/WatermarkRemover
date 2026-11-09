using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Pdf;
using WatermarkRemover.Pdf.Preview;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 180° 烤入复核（v1.16.124）：扫描期把分类器裁成 180° 的页直接烤成 /Rotate 180，但半圈是
/// 分类器的已知混淆点、投影基线又无法交叉验证——真机谭海纯 (2).pdf 的正立首页被 conf 0.857
/// 烤成 180°，缩略图（原始位图）看是正的、大图和 WPS（应用 /Rotate）看是倒的。这里的合同：
/// 落盘后 OCR 复核每个烤入页，烤反的转回归位，烤对的和读不出的原样不动；种子缩略图与烤入
/// 方向一致，条带不再和大图各说各话。
/// </summary>
public class HalfTurnVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-half-turn-" + Guid.NewGuid().ToString("N"));

    public HalfTurnVerifyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Never throws; hands back a small plain PNG so decode/detect work for real.</summary>
    private sealed class FakeRenderer(int pageCount) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
        {
            var info = new SKImageInfo(30, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
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

    /// <summary>The detector queries 0°, 180° first and only then 90°/270° when ambiguous;
    /// the Nth call scores the Nth queried candidate.</summary>
    private sealed class QueueOcr(params int[] charsPerCall) : IOcrEngine
    {
        public string DisplayName => "Queue";

        private int _call;

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            int chars = _call < charsPerCall.Length ? charsPerCall[_call] : 0;
            _call++;
            IReadOnlyList<string> lines = chars == 0 ? Array.Empty<string>() : new[] { new string('字', chars) };
            return Task.FromResult(lines);
        }
    }

    private sealed class UnusableOcr : IOcrEngine
    {
        public string DisplayName => "Unusable";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            throw new InvalidOperationException("本机没有可用的 OCR 引擎");
    }

    private PdfScanPostProcessor NewProcessor(string pdfPath, IOcrEngine ocr, FakeRenderer renderer)
    {
        var file = new PdfDocumentFile(pdfPath, pathChanged: null, debug: null, info: null);
        var cache = new PdfPageRenderCache(renderer, file);
        return new PdfScanPostProcessor(cache, file, ocr, debug: null, info: null);
    }

    /// <summary>Writes a one-page PDF whose page carries the given /Rotate.</summary>
    private string WritePdf(string name, int rotation)
    {
        string path = Path.Combine(_dir, name);
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            page.Rotate = rotation;
            doc.Save(path);
        }
        return path;
    }

    private static int RotationOf(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages[0].Rotate;
    }

    private static async Task DrainAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && (vm.IsLoading || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
        for (int i = 0; i < 300 && !vm.IsOrienting; i++)
            await Task.Delay(10);
        for (int i = 0; i < 300 && vm.IsOrienting; i++)
            await Task.Delay(10);
        for (int i = 0; i < 300 && vm.Thumbnails.Any(t => t.Image is null); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task A_wrongly_baked_half_turn_is_turned_back()
    {
        // 谭海纯 (2) 场景：正立页被烤成 /Rotate 180 → 渲染出来是倒的 → OCR 只在翻转后读得出
        // （straight 2 字、flipped 30 字，决定性半圈）→ 复核转回，/Rotate 归零。
        string pdf = WritePdf("wrong.pdf", 180);
        var processor = NewProcessor(pdf, new QueueOcr(2, 30), new FakeRenderer(1));
        try
        {
            int reverted = await processor.VerifyHalfTurnsAsync(1, [0], 150);

            Assert.Equal(1, reverted);
            Assert.Equal(0, RotationOf(pdf));
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task A_correctly_baked_half_turn_survives_the_verification()
    {
        // 同场借据页对照：conf 0.883 的 180° 是真阳性——渲染出来已正立（straight 30 字），
        // 复核不动它。这正是单纯抬门槛救不了的原因。
        string pdf = WritePdf("right.pdf", 180);
        var processor = NewProcessor(pdf, new QueueOcr(30, 2), new FakeRenderer(1));
        try
        {
            int reverted = await processor.VerifyHalfTurnsAsync(1, [0], 150);

            Assert.Equal(0, reverted);
            Assert.Equal(180, RotationOf(pdf));
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task An_unusable_engine_never_touches_the_file()
    {
        string pdf = WritePdf("locked.pdf", 180);
        var processor = NewProcessor(pdf, new UnusableOcr(), new FakeRenderer(1));
        try
        {
            int reverted = await processor.VerifyHalfTurnsAsync(1, [0], 150);

            Assert.Equal(0, reverted);
            Assert.Equal(180, RotationOf(pdf));
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task The_post_process_verifies_half_turns_without_the_whole_document_pass()
    {
        // 全部页已裁决 + 第 2 页烤入 180°：后处理只复核第 2 页（整份 AutoOrient 会把第 1 页
        // 也拖走）。QueueOcr 只够喂一次探测——若走了整份通道，第 1 页的 /Rotate 会被改写。
        string pdf = Path.Combine(_dir, "scan.pdf");
        using (var doc = new PdfDocument())
        {
            doc.AddPage(); // 第 1 页：未烤入，必须保持 /Rotate 0
            doc.Pages[0].Rotate = 0;
            var second = doc.AddPage();
            second.Rotate = 180; // 第 2 页：烤入 180°
            doc.Save(pdf);
        }

        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(2),
            ocrEngine: new QueueOcr(2, 30), autoOrient: true,
            decidedPages: 2, halfTurnedPageIndices: [1]);
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            Assert.Equal(0, RotationOfPage(pdf, 1)); // 烤反的第 2 页被转回
            Assert.Equal(0, RotationOfPage(pdf, 0)); // 第 1 页没被整份通道碰过
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Seeded_tiles_carry_the_baked_half_turn_so_tile_and_canvas_agree()
    {
        // 种子缩略图是扫描期原始位图（烤入前），直接上条带会和大图各说各话——缩略图看着正、
        // 大图是倒的。烤入页的种子必须带上同一个 180°。
        string pdf = WritePdf("seed.pdf", 0);
        var seeds = new ImageSource[] { MakeBitmapSource(), MakeBitmapSource() };
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(2),
            seedThumbnails: seeds, pageTurns: [0, 2]);
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            var second = Assert.IsType<TransformedBitmap>(vm.Thumbnails[1].Image);
            var rotate = Assert.IsType<RotateTransform>(second.Transform);
            Assert.Equal(180, rotate.Angle);
            Assert.IsNotType<TransformedBitmap>(vm.Thumbnails[0].Image);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Seeded_tiles_carry_a_baked_quarter_turn_so_a_landscape_page_looks_landscape()
    {
        // 用户现场（v1.16.127）：扫描期把页面烤成 /Rotate 90 写成横的，条带里的种子还是竖的。
        // 只处理 180° 不够——90°/270° 也必须照样转，宽高跟着换。
        string pdf = WritePdf("seed-quarter.pdf", 0);
        var seeds = new ImageSource[] { MakeBitmapSource(40, 20), MakeBitmapSource(40, 20) };
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(2),
            seedThumbnails: seeds, pageTurns: [1, 0]);
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            var first = Assert.IsType<TransformedBitmap>(vm.Thumbnails[0].Image);
            Assert.Equal(90, Assert.IsType<RotateTransform>(first.Transform).Angle);
            Assert.Equal(20, first.PixelWidth);  // 横图：宽高互换
            Assert.Equal(40, first.PixelHeight);
            Assert.Equal(40, vm.Thumbnails[1].Image.Width); // 未烤入的页保持原样
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task A_three_quarter_turn_is_normalised_to_the_shortest_clockwise_turn()
    {
        // -1（逆时针一档）与 3（顺时针三档）是同一个朝向：负数不能把 RotateTransform 转反。
        string pdf = WritePdf("seed-negative.pdf", 0);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
            seedThumbnails: [MakeBitmapSource(40, 20)], pageTurns: [-1]);
        try
        {
            await vm.LoadAsync();
            await DrainAsync(vm);

            var tile = Assert.IsType<TransformedBitmap>(vm.Thumbnails[0].Image);
            Assert.Equal(270, Assert.IsType<RotateTransform>(tile.Transform).Angle);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public void Seeds_are_taken_in_pdf_page_order_not_capture_order()
    {
        // 空白页被丢掉、双面扫描背面重排：采集 3 页、PDF 只剩 2 页且第 2 页来自第 1 个采集页。
        // 直接用 LiveThumbnails 的下标会把第 1 页的图贴到第 2 页（连带方向）。
        var live = new ImageSource[] { MakeBitmapSource(), MakeBitmapSource(), MakeBitmapSource() };

        var aligned = PreviewStripCoordinator.AlignSeedsToPdf(live, [2, 1], 2);

        Assert.Same(live[1], aligned[0]);
        Assert.Same(live[0], aligned[1]);
    }

    [Fact]
    public void A_map_that_does_not_line_up_leaves_the_seeds_alone()
    {
        var live = new ImageSource[] { MakeBitmapSource(), MakeBitmapSource() };

        // 页数对不上（空白页被丢但对照表陈旧）、索引越界、没有对照表：一律退回渲染 PDF。
        Assert.Same(live, PreviewStripCoordinator.AlignSeedsToPdf(live, [1, 2, 3], 2));
        Assert.Same(live, PreviewStripCoordinator.AlignSeedsToPdf(live, [1, 9], 2));
        Assert.Same(live, PreviewStripCoordinator.AlignSeedsToPdf(live, null, 2));
        Assert.Empty(PreviewStripCoordinator.AlignSeedsToPdf(Array.Empty<ImageSource>(), [1], 1));
    }

    private static int RotationOfPage(string pdfPath, int pageIndex)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages[pageIndex].Rotate;
    }

    private static BitmapSource MakeBitmapSource(int width = 8, int height = 8)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var source = new BitmapImage();
        using var stream = new MemoryStream(data.ToArray());
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.StreamSource = stream;
        source.EndInit();
        source.Freeze(); // 条带的填充/解码线程都会读它，冻结后跨线程安全（与生产种子一致）
        return source;
    }
}
