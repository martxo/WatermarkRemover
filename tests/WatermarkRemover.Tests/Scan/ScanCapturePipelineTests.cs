using System.Text;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using WatermarkRemover.App.Services;
using WatermarkRemover.Imaging;
using WatermarkRemover.Ocr;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Engine;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.Scan;

/// <summary>
/// v1.16.83 扫描采集流水线（P1 方向裁决前置 + P2 空白判定前置）的文件级与纯函数契约：
/// ① 方向裁决以 /Rotate 元数据写进扫描 PDF，PDFium 渲染侧真的看到转正的页面；
/// ② 空白页在采集期被过滤、全空保首页、页序不动；
/// ③ 采集期判定与原预览侧判定（96dpi 渲染）等价，方向投票在命名图尺度与 90dpi 探针尺度结论一致；
/// ④ /Rotate 经续扫插页与内容流改写（批量去水印）后不丢。
/// </summary>
public class ScanCapturePipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scan-pipeline-{Guid.NewGuid():N}");

    public ScanCapturePipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp best effort */ }
    }

    // ---------- fixtures ----------

    /// <summary>BGRA 扫描原始帧（引擎交给采集管线的形态）。行灰度由 <paramref name="grayByRow"/> 决定，
    /// 可选 <paramref name="xExtent"/> 按行收窄宽度（居中），用于做不对称版式。</summary>
    private static RasterImage Frame(int width, int height, Func<int, byte> grayByRow, Func<int, int, int>? xExtent = null)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            int left = 0, right = width - 1;
            if (xExtent is not null)
            {
                int half = xExtent(y, height);
                left = (width - half) / 2;
                right = left + half - 1;
            }

            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var v = x >= left && x <= right ? grayByRow(y) : (byte)255;
                bgra[o] = v;
                bgra[o + 1] = v;
                bgra[o + 2] = v;
                bgra[o + 3] = 255;
            }
        }

        return new RasterImage(width, height, bgra);
    }

    /// <summary>横条「文本」页：条宽自上而下递减（不对称，180° 可判），行距固定。</summary>
    private static RasterImage TextPage(int width, int height, int bars = 14)
    {
        int period = height / bars;
        int thickness = Math.Max(3, period / 4);
        return Frame(width, height,
            y => (byte)(y % period < thickness ? 0 : 255),
            (y, h) =>
            {
                int bar = Math.Min(bars - 1, y / (h / bars));
                return (int)(width * (0.85 - 0.03 * bar)); // 0.85 → 0.46
            });
    }

    private static RasterImage WhitePage(int width, int height) => Frame(width, height, _ => 255);

    /// <summary>斜纹页。<paramref name="thickness"/> 足够粗（≥4px）时降采样后仍深于墨阈（有内容）；
    /// 细而浅的纹理（gray≥200 或 1px 细线）两侧重采样都会洗掉——与既有产品行为一致。</summary>
    private static RasterImage DiagonalTexturePage(int width, int height, byte gray, int spacing = 8, int thickness = 1)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var d = ((x + y) % spacing + spacing) % spacing;
                var v = d < thickness ? gray : (byte)255;
                bgra[o] = v;
                bgra[o + 1] = v;
                bgra[o + 2] = v;
                bgra[o + 3] = 255;
            }
        }

        return new RasterImage(width, height, bgra);
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Rotate180(SKBitmap source)
    {
        var target = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(source.Width / 2f, source.Height / 2f);
            canvas.RotateDegrees(180);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }
        return target;
    }

    private static SKBitmap Scale(SKBitmap source, int width, int height)
    {
        var target = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return target;
    }

    private string WriteScanPdf(string name, int clockwiseQuarterTurns)
    {
        var document = new ScanDocument
        {
            Pages = new List<ScannedPage> { new() { Image = TextPage(240, 320), Index = 0, SheetIndex = 0 } },
            Settings = new ScanSettings { Resolution = ScanResolution.Dpi300, Color = ColorMode.BlackWhite },
            BaseName = name,
        };
        var options = new PdfBuildOptions { HighCompression = true };
        var encoder = new ScanImageEncoder();
        var encoded = document.Pages
            .Select(p => PdfDocumentBuilder.EncodePage(p, options, encoder, PdfDocumentBuilder.ResolveDpi(document.Settings)))
            .ToList();
        string path = Path.Combine(_dir, name + ".pdf");
        File.WriteAllBytes(path, PdfDocumentBuilder.Build(document, options, encoded, clockwiseQuarterTurns));
        return path;
    }

    private static int RotationOf(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages[0].Rotate;
    }

    private static (int Width, int Height) RenderedSize(string pdfPath, int pageIndex = 0)
    {
        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var reader = lib.GetDocReader(pdfPath, new PageDimensions(0.25));
        using var page = reader.GetPageReader(pageIndex);
        return (page.GetPageWidth(), page.GetPageHeight());
    }

    // ---------- P1：/Rotate 元数据落地 ----------

    [Theory]
    [InlineData(1, 90, true)]   // 顺时针 90°：竖版页渲染为横版
    [InlineData(2, 180, false)] // 180°：仍是竖版
    public void Build_bakes_the_scan_verdict_into_rotate_metadata(int turns, int expectedRotation, bool expectLandscape)
    {
        string path = WriteScanPdf("rotated", turns);

        Assert.Equal(expectedRotation, RotationOf(path));
        var (w, h) = RenderedSize(path);
        Assert.Equal(expectLandscape, w > h);
    }

    [Fact]
    public void An_upright_scan_pdf_stays_clean_without_rotation_metadata()
    {
        // 无裁决（0°）连 /Rotate 键都不写，产物与旧版保持同形。
        string path = WriteScanPdf("upright", 0);
        string raw = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("/Rotate", raw);
        Assert.Equal(0, RotationOf(path));
    }

    // ---------- P2：空白过滤纯函数 ----------

    private static ScanService.TimedEncode Entry(int pageNumber, bool blank, bool faulted = false) => new(
        // faulted：编码任务抛出（payload 缺失但页面并非空白）；blank 且非首页：判定空白不编码。
        Payload: faulted || (blank && pageNumber > 1) ? null : new EncodedScanPage(new byte[] { 1 }, null, 10, 10, 1, 1),
        EncodeMs: 0, PageNumber: pageNumber, Width: 10, Height: 10, ReadMs: 0, IsBlank: blank && !faulted);

    [Fact]
    public void Blank_pages_are_skipped_in_reading_order()
    {
        // 1 正常、2 空白背面、3 正常、4 空白背面 → 空白页不进文件，页序不动。
        var readingOrder = new List<ScanService.TimedEncode?>
        {
            Entry(1, blank: false), Entry(2, blank: true), Entry(3, blank: false), Entry(4, blank: true),
        };

        (var kept, int skipped) = ScanService.SelectPagesForPdf(readingOrder);

        Assert.Equal(2, kept.Count);
        Assert.All(kept, t => Assert.NotNull(t.Payload));
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void An_all_blank_capture_keeps_its_first_page()
    {
        var readingOrder = Enumerable.Range(1, 5)
            .Select(n => (ScanService.TimedEncode?)Entry(n, blank: true))
            .ToList();

        (var kept, int skipped) = ScanService.SelectPagesForPdf(readingOrder);

        Assert.Single(kept); // 第 1 页始终被编码，保底不空手
        Assert.Equal(4, skipped);
    }

    [Fact]
    public void Faulted_encodes_are_skipped_without_counting_as_blank()
    {
        var readingOrder = new List<ScanService.TimedEncode?>
        {
            null, Entry(2, blank: false), Entry(3, blank: false, faulted: true), Entry(4, blank: false),
        };

        (var kept, int skipped) = ScanService.SelectPagesForPdf(readingOrder);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0, skipped);
    }

    // ---------- P2 等价性：采集期降采样判定 vs 96dpi 渲染判定 ----------

    private static bool CaptureVerdict(RasterImage page)
    {
        // 采集管线现行实现：256px 降采样 → PNG → 像素统计。
        var encoder = new ScanImageEncoder();
        byte[] small = encoder.Encode(encoder.Resize(page, 256), "png");
        return BlankPageDetector.IsBlank(small);
    }

    private bool RenderVerdict(RasterImage page, string fixtureName)
    {
        // 旧预览侧实现：整页进 PDF → PDFium 96dpi 渲染 → 同一判定器。
        var encoder = new ScanImageEncoder();
        byte[] png = encoder.Encode(page, "png");
        var content = new PdfPageContent(png, null, page.Width, page.Height,
            page.Width * 72.0 / 300.0, page.Height * 72.0 / 300.0, Array.Empty<PdfTextWord>());
        byte[] pdf = PdfDocumentBuilder.Build("等价性夹具",
            new PdfBuildOptions { HighCompression = false }, 300, new[] { content });
        string path = Path.Combine(_dir, $"{fixtureName}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var lib = DocLib.Instance;
            using var reader = lib.GetDocReader(path, new PageDimensions(96.0 / 72.0));
            using var pageReader = reader.GetPageReader(0);
            byte[] pixels = pageReader.GetImage(new NaiveTransparencyRemover(255, 255, 255));
            var info = new SKImageInfo(pageReader.GetPageWidth(), pageReader.GetPageHeight(),
                SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            return BlankPageDetector.IsBlank(EncodePng(bitmap));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private void AssertVerdictsAgree(RasterImage page, string fixtureName, bool expectedBlank)
    {
        Assert.Equal(expectedBlank, CaptureVerdict(page));
        Assert.Equal(expectedBlank, RenderVerdict(page, fixtureName));
    }

    [Fact]
    public void A_white_page_is_blank_on_both_channels() =>
        AssertVerdictsAgree(WhitePage(1240, 1754), "white", expectedBlank: true);

    [Fact]
    public void A_text_page_has_content_on_both_channels() =>
        AssertVerdictsAgree(TextPage(1240, 1754), "text", expectedBlank: false);

    [Fact]
    public void A_dark_watermark_texture_has_content_on_both_channels()
    {
        // 黑色粗斜纹（4px 厚）：任何合理的降采样后仍深于墨阈——两个通道都判有内容。
        AssertVerdictsAgree(DiagonalTexturePage(1240, 1754, gray: 0, spacing: 8, thickness: 4),
            "dark-texture", expectedBlank: false);
    }

    [Fact]
    public void A_faint_gray_texture_stays_blank_on_both_channels()
    {
        // 浅于墨阈（240 > 200）的底纹在两个通道都不算墨——与既有产品行为一致，不因换通道而改变。
        AssertVerdictsAgree(DiagonalTexturePage(1240, 1754, gray: 240), "faint-texture", expectedBlank: true);
    }

    // ---------- P1 等价性：命名图尺度 vs 90dpi 探针尺度 ----------

    /// <summary>几何假引擎：解码候选图，找暗色横带作为文本行框——按真实像素工作，天然尺度一致，
    /// 用于验证投票管线（空白短路/横版守卫/旋转/几何打分）在不同分辨率下结论稳定。</summary>
    private sealed class BandGeometryOcr : IBoundedOcrEngine
    {
        public string DisplayName => "BandGeometry";

        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            RecognizeWithBoundsAsync(imageBytes).ContinueWith(t =>
                (IReadOnlyList<string>)t.Result.Select(l => l.Text).ToList());

        public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
        {
            Calls++;
            using SKBitmap? bitmap = SKBitmap.Decode(imageBytes);
            var lines = new List<RecognizedLine>();
            if (bitmap is null || bitmap.PeekPixels() is not { } pixmap)
                return Task.FromResult<IReadOnlyList<RecognizedLine>>(lines);

            ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan();
            int width = bitmap.Width, height = bitmap.Height, bpp = bitmap.BytesPerPixel;

            int bandStart = -1, minX = 0, maxX = 0;
            for (int y = 0; y <= height; y++)
            {
                bool dark = false;
                int rowMin = 0, rowMax = 0;
                if (y < height)
                    dark = RowInk(pixels, y, width, bpp, out rowMin, out rowMax);

                if (dark && bandStart < 0)
                {
                    bandStart = y;
                    minX = rowMin;
                    maxX = rowMax;
                }
                else if (dark)
                {
                    minX = Math.Min(minX, rowMin);
                    maxX = Math.Max(maxX, rowMax);
                }
                else if (bandStart >= 0)
                {
                    // 暗带在 y-1 结束（y 本行是亮的，或已越过图底）。
                    if (y - bandStart >= 2)
                        lines.Add(new RecognizedLine("字", new RectD(
                            minX / (double)width,
                            bandStart / (double)height,
                            Math.Max(1, maxX - minX) / (double)width,
                            (y - bandStart) / (double)height)));
                    bandStart = -1;
                }
            }

            return Task.FromResult<IReadOnlyList<RecognizedLine>>(OrderByReading(lines));
        }

        /// <summary>真实有界引擎按「阅读序」返回行：正立页自上而下（Y 递增），倒置页的阅读序
        /// 在图像坐标里是自下而上（Y 递减）——这正是几何通道判 180° 的依据。这里用横条宽度
        /// （宽→窄 = 阅读先后）模拟阅读序，与内容方向一致。</summary>
        private static IReadOnlyList<RecognizedLine> OrderByReading(List<RecognizedLine> bands) =>
            bands.OrderByDescending(l => l.Bounds is { } b ? b.Width : 0).ToList();

        private static bool RowInk(ReadOnlySpan<byte> pixels, int y, int width, int bpp, out int minX, out int maxX)
        {
            int inked = 0, sampled = 0;
            minX = width;
            maxX = 0;
            int rowStart = y * width * bpp;
            for (int x = 0; x < width; x += 2)
            {
                int p = rowStart + x * bpp;
                int gray = (pixels[p + 2] * 299 + pixels[p + 1] * 587 + pixels[p] * 114) / 1000;
                sampled++;
                if (gray < 128)
                {
                    inked++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }

            return sampled > 0 && inked / (double)sampled > 0.02;
        }
    }

    [Fact]
    public async Task Orientation_verdict_is_stable_across_naming_and_probe_scales()
    {
        // 命名图：A4@300dpi 命名链 resizing 到最长边 2500（1764×2500）；探针：90dpi（≈744×1052）。
        using var namingScale = TextPageBitmap(1764, 2500);
        using var probeScale = Scale(namingScale, 744, 1042);
        using var flippedNaming = Rotate180(namingScale);
        using var flippedProbe = Scale(flippedNaming, 744, 1042);

        var engine = new BandGeometryOcr();
        var detector = new PdfOrientationDetector(engine);

        Assert.Equal(0, await detector.DetectClockwiseStepsAsync(EncodePng(namingScale)));
        Assert.Equal(0, await detector.DetectClockwiseStepsAsync(EncodePng(probeScale)));
        Assert.Equal(2, await detector.DetectClockwiseStepsAsync(EncodePng(flippedNaming)));
        Assert.Equal(2, await detector.DetectClockwiseStepsAsync(EncodePng(flippedProbe)));
        Assert.True(engine.Calls >= 8, $"几何通道应真实投票（实际 {engine.Calls} 次识别）");
    }

    /// <summary>SKBitmap 版文本页（不对称横条，180° 可判）。</summary>
    private static SKBitmap TextPageBitmap(int width, int height, int bars = 14)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            var paint = new SKPaint { Color = SKColors.Black };
            int period = height / bars;
            int thickness = Math.Max(3, period / 4);
            for (int i = 0; i < bars; i++)
            {
                float widthFraction = 0.85f - 0.03f * i;
                float barWidth = width * widthFraction;
                canvas.DrawRect((width - barWidth) / 2f, i * (float)period, barWidth, thickness, paint);
            }
        }
        return bitmap;
    }

    // ---------- /Rotate 通道回归：续扫插页与内容流改写 ----------

    [Fact]
    public void Appended_pages_keep_their_rotation_metadata()
    {
        // 续扫（AppendDetailed）把带 /Rotate 的新扫描页并入旧文档：元数据必须原样带过去。
        string target = WriteScanPdf("target", 0);
        string source = WriteScanPdf("source", 1);

        PdfInsertResult result = PdfPageInserter.AppendDetailed(target, source);

        Assert.Equal(1, result.AddedPages);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.InformationOnly);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(0, doc.Pages[0].Rotate);
        Assert.Equal(90, doc.Pages[1].Rotate);
        var (w, h) = RenderedSize(target, pageIndex: 1);
        Assert.True(w > h, "插入页渲染应为横版（/Rotate 90 生效）");
    }

    [Fact]
    public void Content_stream_rewrite_channel_keeps_the_rotation_metadata()
    {
        // 批量去水印走 PdfSharp Modify 打开 → 内容流改写 → 另存。检测在内容空间进行（与
        // /Rotate 无关），但元数据必须在往返后原样保留。
        string path = WriteScanPdf("dewatermark", 2);
        string copy = Path.Combine(_dir, "roundtrip.pdf");

        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            doc.Save(copy);
        }

        Assert.Equal(180, RotationOf(copy));
        var (w, h) = RenderedSize(copy);
        Assert.True(h > w, "180° 的竖版页渲染仍为竖版");
    }
}
