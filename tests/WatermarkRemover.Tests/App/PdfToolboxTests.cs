using WatermarkRemover.App;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Ocr;
using WatermarkRemover.Pdf;
using WatermarkRemover.Pdf.Rendering;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 「PDF 工具」统一工作台：去水印与 OCR 不再各占一个菜单，合并为一个 feature。
/// 钉死三件事：section id 沿用 watermark.badge（历史记录/任务日志零迁移）、按扩展名路由、
/// OCR 能力从记录行发起（MainViewModel 侧），导航里不再有独立 OCR 菜单。
/// </summary>
public class PdfToolboxTests
{
    private sealed class StubOcrEngine : IOcrEngine
    {
        public string DisplayName => "stub";
        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class StubPageRenderer : IPdfPageRenderer
    {
        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            Task.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());
        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) => Task.FromResult(Array.Empty<byte>());
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(1);
        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private static PdfToolboxFeature NewToolbox() => new(
        new WatermarkFeature(new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() }),
        new OcrFeature(new StubPageRenderer(), new StubOcrEngine(), imageLoader: _ => new byte[] { 0x01 }));

    [Fact]
    public void The_workbench_keeps_the_watermark_section_id()
    {
        // 任务日志与历史记录的 OwnerFeatureId 是 watermark.badge：id 一变它们就成了孤儿。
        var toolbox = NewToolbox();
        Assert.Equal("watermark.badge", toolbox.Id);
        Assert.Equal("PDF 工具", toolbox.DisplayName);
    }

    [Fact]
    public void Pdf_goes_to_watermark_and_images_go_to_ocr()
    {
        var toolbox = NewToolbox();

        Assert.True(toolbox.CanHandle("C:\\a\\文档.pdf"));
        Assert.True(toolbox.CanHandle("C:\\a\\照片.png"));
        Assert.True(toolbox.CanHandle("C:\\a\\照片.jpg"));
        Assert.False(toolbox.CanHandle("C:\\a\\压缩包.zip"));

        Assert.Contains(".pdf", toolbox.InputExtensions);
        Assert.Contains(".png", toolbox.InputExtensions);

        Assert.IsType<WatermarkFeature>(toolbox.RouteFor("C:\\a\\文档.pdf"));
        Assert.IsType<OcrFeature>(toolbox.RouteFor("C:\\a\\照片.png"));
        // 大小写不敏感
        Assert.IsType<OcrFeature>(toolbox.RouteFor("C:\\a\\照片.PNG"));
    }

    [Fact]
    public void The_row_button_always_routes_to_ocr()
    {
        // 「识别文字」按钮曾走 RouteFor，PDF 行被送去水印检测（5ms 空转「未检测到水印」）。
        // 该按钮必须无条件拿到 OCR 处理器。
        var toolbox = NewToolbox();
        Assert.IsType<OcrFeature>(toolbox.Ocr);
    }
}
