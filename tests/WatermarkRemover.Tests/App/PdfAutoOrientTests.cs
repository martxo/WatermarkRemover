using PdfSharpCore.Pdf.IO;
using SkiaSharp;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Auto-orientation end to end on a real file: the strip renders page one, the OCR engine votes
/// over four quarter turns, and a decisive verdict rewrites every page's /Rotate in one save.
/// These tests pin the file-level contract (the metadata really lands on disk) and the quiet
/// contract (an indecisive or unusable engine never touches the file).
/// </summary>
public class PdfAutoOrientTests
{
    /// <summary>Never throws; hands back a small plain PNG so decode/detect work for real.</summary>
    private sealed class FakeRenderer(int pageCount) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi)
        {
            // Portrait canvas — the orientation detector exempts landscape pages outright.
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

    /// <summary>LoadAsync's orientation pass is fire-and-forget: wait for it to start, then to
    /// finish — the IsOrienting flag brackets the whole detection + rewrite.</summary>
    private static async Task DrainAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && (vm.IsLoading || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
        for (int i = 0; i < 300 && !vm.IsOrienting; i++)
            await Task.Delay(10);
        for (int i = 0; i < 300 && vm.IsOrienting; i++)
            await Task.Delay(10);
    }

    private static int RotationOf(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages[0].Rotate;
    }

    [Fact]
    public async Task An_upside_down_scan_is_righted_in_place_after_load()
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-orient-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        try
        {
            // Call order is 0°, 180°, then 90°/270° only when the vertical pair is ambiguous:
            // straight reads 2 chars, the flipped candidate reads 30 → decisive upside-down.
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
                ocrEngine: new QueueOcr(2, 30), autoOrient: true);
            await vm.LoadAsync();
            await DrainAsync(vm);

            Assert.Equal(180, RotationOf(pdf)); // two clockwise quarter turns were written
            Assert.Equal("已自动摆正。", vm.StatusText);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task An_already_upright_scan_is_never_touched()
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-orient-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        try
        {
            // turn 0 reads 30 chars by a wide margin — the page is already correct.
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
                ocrEngine: new QueueOcr(30, 2, 2, 2), autoOrient: true);
            await vm.LoadAsync();
            await DrainAsync(vm);

            Assert.Equal(0, RotationOf(pdf));
            Assert.Equal(string.Empty, vm.StatusText);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task An_unusable_engine_leaves_the_file_and_the_ui_alone()
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-orient-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        try
        {
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
                ocrEngine: new UnusableOcr(), autoOrient: true);
            await vm.LoadAsync();
            await DrainAsync(vm);

            Assert.Equal(0, RotationOf(pdf));
            Assert.Equal(string.Empty, vm.StatusText); // quiet failure — nothing to dismiss
            Assert.NotNull(vm.CurrentImage); // the strip still works
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
