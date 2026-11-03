using PdfSharp.Pdf.IO;
using SkiaSharp;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Ocr;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// v1.16.83 P1：第 1 页方向投票只做一次——命名链复用扫描期裁决（不再重复付 2-4 个 OCR pass），
/// 方向已裁决的扫描行打开预览时跳过自动摆正/空白清理（手动按钮保留）。
/// </summary>
public class ScanEarlyVerdictTests
{
    private static byte[] PortraitInkPng(int width = 30, int height = 40)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(5, 4, width - 10, 3, paint); // 一条横「文本」：非空白、竖版
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>记录每次识别收到的图像尺寸与首次调用形态。观测点说明：带裁决时命名链不再投票，
    /// 第一个 OCR 调用是定位器的顶部条带裁切（非整图）；不带裁决时方向投票先行，第一个调用
    /// 必是 30×40 整图。整页 OCR（40×30 vs 30×40）则证明旋转是否真的被应用。</summary>
    private sealed class DimensionRecordingOcr : IBoundedOcrEngine
    {
        public int Calls { get; private set; }
        public List<(int Width, int Height)> SeenSizes { get; } = new();
        public (int Width, int Height) FirstSeen => SeenSizes.Count > 0 ? SeenSizes[0] : default;
        public bool FirstSeenIsFullImage => FirstSeen == (30, 40);
        public bool SawFull(int width, int height) => SeenSizes.Contains((width, height));

        public string DisplayName => "DimensionRecording";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            RecognizeWithBoundsAsync(imageBytes).ContinueWith(t =>
                (IReadOnlyList<string>)t.Result.Select(l => l.Text).ToList());

        public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
        {
            Calls++;
            using var bitmap = SKBitmap.Decode(imageBytes);
            SeenSizes.Add(bitmap is null ? (0, 0) : (bitmap.Width, bitmap.Height));
            return Task.FromResult<IReadOnlyList<RecognizedLine>>(Array.Empty<RecognizedLine>());
        }
    }

    private sealed class ThrowingOcr : IOcrEngine
    {
        public string DisplayName => "Throwing";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            throw new ArgumentException("引擎彻底不可用");
    }

    private static ContentNamingService CreateService(IOcrEngine ocr) =>
        new(new AppSettings { AutoNameByContent = true }, new NoRenderer(), ocr);

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

    [Fact]
    public async Task The_precomputed_verdict_skips_the_vote_and_turns_the_image()
    {
        // 裁决=1：链内不再投票（首个调用是定位器裁带而非 30×40 整图投票），
        // 且整页 OCR 收到的是转过 90° 的横版图——旋转真的被应用了。
        var ocr = new DimensionRecordingOcr();
        var service = CreateService(ocr);

        await service.TrySuggestFromImageAsync(PortraitInkPng(), preOrientedSteps: 1);

        Assert.False(ocr.FirstSeenIsFullImage, "不应再付方向投票（首个调用应是定位器裁带）");
        Assert.True(ocr.SawFull(40, 30), "整页识别应收到转过 90° 的横版图");
        Assert.False(ocr.SawFull(30, 40), "原始竖版整图不应再被识别");
    }

    [Fact]
    public async Task The_verdict_of_zero_skips_the_vote_but_never_turns_the_image()
    {
        var ocr = new DimensionRecordingOcr();
        var service = CreateService(ocr);

        await service.TrySuggestFromImageAsync(PortraitInkPng(), preOrientedSteps: 0);

        Assert.False(ocr.FirstSeenIsFullImage, "0° 同样跳过投票");
        Assert.True(ocr.SawFull(30, 40), "0° 不旋转：整页识别仍见原方向图");
        Assert.False(ocr.SawFull(40, 30));
    }

    [Fact]
    public async Task Without_a_precomputed_verdict_the_chain_detects_as_before()
    {
        // 无裁决（null）：保持旧行为——链内先投票，第一个调用必是 30×40 整图。
        var ocr = new DimensionRecordingOcr();
        var service = CreateService(ocr);

        await service.TrySuggestFromImageAsync(PortraitInkPng(), preOrientedSteps: null);

        Assert.True(ocr.FirstSeenIsFullImage,
            $"首个调用应是方向投票的整图（实际 {ocr.FirstSeen.Width}×{ocr.FirstSeen.Height}）");
    }

    [Fact]
    public async Task DetectOrientationAsync_swallows_a_broken_engine()
    {
        // 裁决失败绝不抛出：返回 null，预览侧自动摆正仍是安全网。
        var service = CreateService(new ThrowingOcr());

        Assert.Null(await service.DetectOrientationAsync(PortraitInkPng()));
    }

    // ---------- 方向已裁决的扫描行：预览不再自动摆正 ----------

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
            return Task.FromResult(data.ToArray());
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    /// <summary>The detector queries 0°, 180° first; the Nth call scores the Nth candidate.</summary>
    private sealed class QueueOcr(params int[] charsPerCall) : IOcrEngine
    {
        public string DisplayName => "Queue";

        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            int chars = Calls < charsPerCall.Length ? charsPerCall[Calls] : 0;
            Calls++;
            IReadOnlyList<string> lines = chars == 0 ? Array.Empty<string>() : new[] { new string('字', chars) };
            return Task.FromResult(lines);
        }
    }

    private static async Task WaitForLoadAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && (vm.IsLoading || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
        await Task.Delay(150); // 若（不应发生的）自动后处理被启动，给它时间现形
    }

    [Fact]
    public async Task A_settled_scan_row_runs_no_automatic_post_process()
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-settled-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        try
        {
            // 倒置件 + 会翻页的引擎：若自动摆正仍被触发，Calls>0 且 /Rotate 被写 180。
            var ocr = new QueueOcr(2, 30);
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
                ocrEngine: ocr, autoOrient: false, autoCleanBlank: false);
            await vm.LoadAsync();
            await WaitForLoadAsync(vm);

            Assert.Equal(0, ocr.Calls); // 一次方向投票都没付
            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.InformationOnly);
            Assert.Equal(0, doc.Pages[0].Rotate); // 文件未被改动
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task An_unsettled_scan_row_still_self_heals_through_the_strip()
    {
        // 裁决缺失（引擎不可用等）时旧行为保留：预览自动摆正照常兜底。
        string pdf = Path.Combine(Path.GetTempPath(), "wm-unsettled-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        try
        {
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
                ocrEngine: new QueueOcr(2, 30), autoOrient: true, autoCleanBlank: false);
            await vm.LoadAsync();
            for (int i = 0; i < 300 && vm.IsOrienting; i++)
                await Task.Delay(10);
            for (int i = 0; i < 300 && (vm.IsLoading || vm.Thumbnails.Any(t => t.Image is null)); i++)
                await Task.Delay(10);

            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.InformationOnly);
            Assert.Equal(180, doc.Pages[0].Rotate); // 兜底路径照常生效
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
