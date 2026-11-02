using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using WatermarkRemover.Imaging.Screenshot;
using WatermarkRemover.Pdf.Annotation;

namespace WatermarkRemover.Tests.Pdf;

/// <summary>
/// The three save tiers are a file-level promise, so they are proven at the pixel level: build a
/// PDF, write a tier, render the result with the same PDFium channel the preview uses and check
/// what actually lands on the page.
/// <list type="bullet">
/// <item>档① overlay: the mark is visible AND the original content survives underneath (alpha);</item>
/// <item>档② burn: the page content is REPLACED by the image (the old drawing is gone);</item>
/// <item>档③ image PDF: one image page per input, no text layer.</item>
/// </list>
/// Rotated pages are covered too: the annotation is drawn in display orientation, so the export
/// must counter-rotate it into page space and let the viewer's /Rotate bring it back.
/// </summary>
public sealed class PdfAnnotationExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-anno-" + Guid.NewGuid().ToString("N"));

    public PdfAnnotationExporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>200×200pt page with a solid red square at raster (20,20)-(80,80) — top-left.
    /// PdfSharp's page canvas is y-down, so XRect(20,20,60,60) lands in the raster's top-left.</summary>
    private string WriteRedSquarePdf()
    {
        string path = Path.Combine(_dir, "red.pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(200);
        page.Height = XUnit.FromPoint(200);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawRectangle(new XSolidBrush(XColors.Red), new XRect(20, 20, 60, 60));
        doc.Save(path);
        return path;
    }

    /// <summary>Portrait MediaBox 100×200 with /Rotate 90 — the viewer shows it as 200×100
    /// landscape. A blue square at canvas (60,60)-(90,90) appears in the display's right-centre
    /// area (125,75) after the viewer's quarter turn, clear of the annotation mark.</summary>
    private string WriteRotatedPdf()
    {
        string path = Path.Combine(_dir, "rotated.pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(100);
        page.Height = XUnit.FromPoint(200);
        page.Rotate = 90;
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawRectangle(new XSolidBrush(XColors.Blue), new XRect(60, 60, 30, 30));
        doc.Save(path);
        return path;
    }

    /// <summary>Blue rectangle mark in the annotation coordinate space (display raster pixels).</summary>
    private static ScreenshotAnnotation Mark(float x1, float y1, float x2, float y2) => new()
    {
        Kind = ScreenshotAnnotationKind.Rectangle,
        Points = new[] { new ScreenshotPointF(x1, y1), new ScreenshotPointF(x2, y2) },
        ColorArgb = 0xFF1E88E5,
        Thickness = 8f,
    };

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>One raster at scale 1 (one PDF point per pixel) — the same channel the preview binds.</summary>
    private static (int Width, int Height, byte[] Pixels) RenderAt(string path, int pageIndex = 0, double scale = 1.0)
    {
        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var reader = lib.GetDocReader(path, new PageDimensions(scale));
        var page = reader.GetPageReader(pageIndex);
        return (page.GetPageWidth(), page.GetPageHeight(),
            page.GetImage(new NaiveTransparencyRemover(255, 255, 255)));
    }

    private static (byte B, byte G, byte R) Pixel(byte[] pixels, int width, int x, int y)
    {
        int offset = y * width * 4 + x * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    /// <summary>颜色断言：PDFium 渲染有抗锯齿/白底舍入，按容差比通道。</summary>
    private static void AssertColor(
        (byte B, byte G, byte R) actual, byte b, byte g, byte r, string what, int tolerance = 45)
    {
        Assert.True(
            Math.Abs(actual.B - b) <= tolerance &&
            Math.Abs(actual.G - g) <= tolerance &&
            Math.Abs(actual.R - r) <= tolerance,
            $"{what}: 期望约 B{b} G{g} R{r}，实际 B{actual.B} G{actual.G} R{actual.R}");
    }

    // --- 档①：保留原文 + 叠加批注 --------------------------------------------------

    [Fact]
    public void Overlay_draws_the_mark_and_keeps_the_original_content_underneath()
    {
        string pdf = WriteRedSquarePdf();
        // 标注框压在红方块上：笔画不透明、框内透明——红底必须透出来。
        byte[] layer = EncodePng(ScreenshotComposer.ComposeAnnotationLayer(
            200, 200, new[] { Mark(30, 30, 90, 90) }));

        string result = PdfAnnotationExporter.OverlayPages(pdf, new[] { new PdfAnnotationPage(0, layer) });

        Assert.Equal(pdf, result); // written in place
        var (width, height, pixels) = RenderAt(pdf);
        Assert.Equal(200, width);
        Assert.Equal(200, height);

        // 笔画在（蓝色：0xFF1E88E5）。
        AssertColor(Pixel(pixels, width, 30, 60), 229, 136, 30, "叠加层笔画");
        // 框内透明处仍是原文的红色（叠加层没有挡住原文）。
        AssertColor(Pixel(pixels, width, 60, 60), 0, 0, 255, "原文红方块");
    }

    [Fact]
    public void Overlay_on_a_rotated_page_lands_the_mark_where_the_user_drew_it()
    {
        string pdf = WriteRotatedPdf();
        // 用户在「显示方向」（200×100 横向）的左上角画标注；页面本身 /Rotate 90。
        byte[] layer = EncodePng(ScreenshotComposer.ComposeAnnotationLayer(
            200, 100, new[] { Mark(10, 10, 50, 50) }));

        PdfAnnotationExporter.OverlayPages(pdf, new[] { new PdfAnnotationPage(0, layer) });

        var (width, height, pixels) = RenderAt(pdf);
        Assert.Equal(200, width); // 显示方向：横向（/Rotate 由阅读器加）
        Assert.Equal(100, height);

        // 标注落在显示左上角（用户画的位置）。
        AssertColor(Pixel(pixels, width, 10, 30), 229, 136, 30, "旋转页标注落点");
        // 原文蓝方块随页面一起转到显示右中区域，且没被标注挡住。
        AssertColor(Pixel(pixels, width, 125, 75), 255, 0, 0, "旋转页原文蓝方块");
    }

    [Fact]
    public void Overlay_only_touches_the_pages_it_is_given()
    {
        string pdf = WriteTwoPagePdf();
        byte[] layer = EncodePng(ScreenshotComposer.ComposeAnnotationLayer(
            200, 200, new[] { Mark(30, 30, 90, 90) }));

        PdfAnnotationExporter.OverlayPages(pdf, new[] { new PdfAnnotationPage(1, layer) });

        var (w0, _, p0) = RenderAt(pdf, 0);
        var (w1, _, p1) = RenderAt(pdf, 1);
        // 第 1 页没有标注：全白（PDFium 白底渲染出来是 254，按容差比）。
        Assert.Equal(200, w0);
        AssertColor(Pixel(p0, w0, 60, 60), 255, 255, 255, "第 1 页应保持原样");
        // 第 2 页有标注笔画。
        Assert.Equal(200, w1);
        AssertColor(Pixel(p1, w1, 30, 60), 229, 136, 30, "第 2 页标注笔画");
    }

    [Fact]
    public void A_locked_original_overlays_into_a_copy_beside_it()
    {
        string pdf = WriteRedSquarePdf();
        byte[] original = File.ReadAllBytes(pdf);
        byte[] layer = EncodePng(ScreenshotComposer.ComposeAnnotationLayer(
            200, 200, new[] { Mark(30, 30, 90, 90) }));

        using var hold = new FileStream(pdf, FileMode.Open, FileAccess.Read, FileShare.Read);
        string result = PdfAnnotationExporter.OverlayPages(pdf, new[] { new PdfAnnotationPage(0, layer) });

        Assert.False(string.Equals(pdf, result, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("(已标注).pdf", result, StringComparison.Ordinal);
        Assert.True(File.Exists(result));
        Assert.Equal(original, File.ReadAllBytes(pdf)); // 原文件一个字节都没动
        Assert.False(File.Exists(pdf + ".edit.tmp"));
    }

    // --- 档②：烧录定稿 --------------------------------------------------------------

    [Fact]
    public void Burn_replaces_the_page_content_with_the_image()
    {
        string pdf = WriteRedSquarePdf();
        // 烧录图：整页纯绿（模拟「页面 + 标注」的合成结果）。
        using var green = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(green)) canvas.Clear(new SKColor(30, 160, 60));

        string result = PdfAnnotationExporter.BurnPages(pdf, new[] { new PdfAnnotationPage(0, EncodePng(green)) });

        Assert.Equal(pdf, result);
        var (width, _, pixels) = RenderAt(pdf);
        // 原来的红方块不见了：整页就是那张图（内容被替换，不是叠加）。
        AssertColor(Pixel(pixels, width, 50, 50), 60, 160, 30, "烧录图绿底（原红方块处）");
        AssertColor(Pixel(pixels, width, 100, 100), 60, 160, 30, "烧录图绿底（页面中心）");
    }

    [Fact]
    public void Burn_on_a_rotated_page_keeps_the_image_upright_for_the_viewer()
    {
        string pdf = WriteRotatedPdf();
        // 显示方向的图：上半蓝、下半绿。烧录后阅读器加 /Rotate 90，看到的必须还是上蓝下绿。
        using var image = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(new SKColor(30, 160, 60));
            canvas.DrawRect(new SKRect(0, 0, 200, 40), new SKPaint { Color = new SKColor(30, 90, 220) });
        }

        PdfAnnotationExporter.BurnPages(pdf, new[] { new PdfAnnotationPage(0, EncodePng(image)) });

        var (width, height, pixels) = RenderAt(pdf);
        Assert.Equal(200, width);
        Assert.Equal(100, height);
        AssertColor(Pixel(pixels, width, 100, 20), 220, 90, 30, "烧录图上蓝（显示方向）");
        AssertColor(Pixel(pixels, width, 100, 80), 60, 160, 30, "烧录图下绿（显示方向）");
    }

    // --- 档③：图片型 PDF ------------------------------------------------------------

    [Fact]
    public void Image_pdf_holds_one_image_page_per_input_and_no_text_layer()
    {
        using var first = new SKBitmap(new SKImageInfo(120, 160, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var second = new SKBitmap(new SKImageInfo(120, 160, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(first)) canvas.Clear(new SKColor(200, 40, 40));
        using (var canvas = new SKCanvas(second)) canvas.Clear(new SKColor(40, 40, 200));

        byte[] pdf = PdfAnnotationExporter.BuildImagePdf("导出", new[]
        {
            new ImagePdfPage(EncodePng(first), 120, 160, 150),
            new ImagePdfPage(EncodePng(second), 120, 160, 150),
        });

        string path = Path.Combine(_dir, "images.pdf");
        File.WriteAllBytes(path, pdf);

        Assert.Equal(2, PageCount(path));
        // 页面尺寸来自图像像素：120px @150dpi = 57.6×76.8pt；按 2px/pt 渲染便于取中心点。
        var (w0, h0, p0) = RenderAt(path, 0, 2.0);
        Assert.Equal(115, w0);
        Assert.Equal(153, h0);
        AssertColor(Pixel(p0, w0, 57, 76), 40, 40, 200, "图片型 PDF 第 1 页");
        var (w1, h1, p1) = RenderAt(path, 1, 2.0);
        Assert.Equal(115, w1);
        Assert.Equal(153, h1);
        AssertColor(Pixel(p1, w1, 57, 76), 200, 40, 40, "图片型 PDF 第 2 页");

        // 图片型 PDF 没有文本层：页面资源里只有图像 XObject，没有字体。
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        PdfPage page = doc.Pages[0];
        Assert.True(page.Resources is { } resources && resources.Elements.ContainsKey("/XObject"),
            "图片型 PDF 的页面应只带图像 XObject");
    }

    // --- fixtures -------------------------------------------------------------------

    private string WriteTwoPagePdf()
    {
        string path = Path.Combine(_dir, "two.pdf");
        using var doc = new PdfDocument();
        for (int i = 0; i < 2; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(200);
            page.Height = XUnit.FromPoint(200);
        }

        doc.Save(path);
        return path;
    }

    private static int PageCount(string path)
    {
        var lib = DocLib.Instance;
        using var reader = lib.GetDocReader(path, new PageDimensions(0.25));
        return reader.GetPageCount();
    }
}
