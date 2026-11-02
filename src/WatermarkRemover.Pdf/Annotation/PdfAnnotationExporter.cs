using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace WatermarkRemover.Pdf.Annotation;

/// <summary>一页要写回 PDF 的图像：页码 + PNG（标注层或整页烧录图，由调用方按档位给出）。</summary>
public readonly record struct PdfAnnotationPage(int PageIndex, byte[] Png);

/// <summary>图片型 PDF 的一页：PNG + 像素尺寸 + 几何 DPI（点 = 像素 × 72 / DPI）。</summary>
public readonly record struct ImagePdfPage(byte[] Png, int PixelWidth, int PixelHeight, double Dpi);

/// <summary>
/// 把标注写回 PDF 的三个出口（保存策略三档，市调结论 2026-11-02）：
/// <list type="bullet">
/// <item>档① <see cref="OverlayPages"/>：标注层透明 PNG 叠回页面——原文与文本层保留、可检索（默认）；</item>
/// <item>档② <see cref="BurnPages"/>：合成图烧录为页面内容——定稿/防改，仅传入的页，其余页不动；</item>
/// <item>档③ <see cref="BuildImagePdf"/>：整文档导出图片型 PDF——脱敏安全出口（底层文字不可再取）。</item>
/// </list>
/// ①②都走 <see cref="PdfDocumentEditor"/> 的「先写边文件、成功后整体替换」机制：原文件被
/// 其他程序占用时自动另存副本并返回新路径（与旋转/删除同一模式），调用方据此跟随。
/// 旋转页的处理：标注图是「显示方向」的光栅，先按页面 /Rotate 逆时针转回「页面方向」再画到
/// MediaBox 上——阅读器再加 /Rotate 后，标注在屏幕上的方向就是用户画的时候看到的方向。
/// </summary>
public static class PdfAnnotationExporter
{
    /// <summary>
    /// 档①：把每页的标注层 PNG 叠加到页面上（追加内容流，不动原文）。返回持有结果的文件路径：
    /// 正常是 <paramref name="pdfPath"/> 本身，原文件被占用时是「原名 (已标注).pdf」副本。
    /// </summary>
    public static string OverlayPages(string pdfPath, IReadOnlyList<PdfAnnotationPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("没有要叠加的页面", nameof(pages));

        return PdfDocumentEditor.EditDocument(pdfPath, doc =>
        {
            foreach (PdfAnnotationPage page in pages)
            {
                if (page.PageIndex < 0 || page.PageIndex >= doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(pages),
                        $"页码超出范围：第 {page.PageIndex + 1} 页（共 {doc.PageCount} 页）。");

                PdfPage target = doc.Pages[page.PageIndex];
                using SKBitmap image = Load(page.Png);
                using SKBitmap? turned = TurnToPageSpace(image, target.Rotate);
                DrawOverlayPage(doc, target, turned ?? image);
            }
        }, keepCopyOnLock: true, fallbackSuffix: "已标注");
    }

    /// <summary>
    /// 档②：把每页的烧录图（整页合成图）写为页面内容——旧内容（文本/绘图）整体移除，
    /// 页面从此是图像，不可再检索、不可再改。返回路径规则同 <see cref="OverlayPages"/>。
    /// </summary>
    public static string BurnPages(string pdfPath, IReadOnlyList<PdfAnnotationPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("没有要烧录的页面", nameof(pages));

        return PdfDocumentEditor.EditDocument(pdfPath, doc =>
        {
            foreach (PdfAnnotationPage page in pages)
            {
                if (page.PageIndex < 0 || page.PageIndex >= doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(pages),
                        $"页码超出范围：第 {page.PageIndex + 1} 页（共 {doc.PageCount} 页）。");

                PdfPage target = doc.Pages[page.PageIndex];
                using SKBitmap image = Load(page.Png);
                using SKBitmap? turned = TurnToPageSpace(image, target.Rotate);
                BurnPage(doc, target, turned ?? image);
            }
        }, keepCopyOnLock: true, fallbackSuffix: "已烧录");
    }

    /// <summary>
    /// 档③：把整文档导出为图片型 PDF（每页一张图，无文本层）——「视觉遮盖不可靠」场景的
    /// 彻底出口：黑块/马赛克底下的文字随光栅化一起消失，再也搜不出来、拷不出去。
    /// </summary>
    public static byte[] BuildImagePdf(string title, IReadOnlyList<ImagePdfPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("没有可导出的页面", nameof(pages));

        PdfSharpRuntime.EnsureInitialized();

        var contents = new PdfPageContent[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            ImagePdfPage page = pages[i];
            contents[i] = new PdfPageContent(
                page.Png,
                null,
                page.PixelWidth,
                page.PixelHeight,
                PageGeometry.PixelsToPoints(page.PixelWidth, page.Dpi),
                PageGeometry.PixelsToPoints(page.PixelHeight, page.Dpi),
                Array.Empty<PdfTextWord>());
        }

        // 新文档：页面尺寸由图像像素给出（图片型 PDF 的页面就是这张图），无 /Rotate。
        return PdfDocumentBuilder.Build(title ?? string.Empty, new PdfBuildOptions(), pages[0].Dpi, contents);
    }

    // --- 绘制 ---------------------------------------------------------------------

    /// <summary>档①：追加一条内容流，把（可能带 alpha 的）标注层画满整页。</summary>
    private static void DrawOverlayPage(PdfDocument doc, PdfPage page, SKBitmap image)
    {
        using var stream = new MemoryStream(EncodePng(image));
        using var ximage = XImage.FromStream(stream);
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        gfx.DrawImage(ximage, 0, 0, page.Width.Point, page.Height.Point);
    }

    /// <summary>档②：换掉整页内容，只留一张画满 MediaBox 的不透明图。</summary>
    private static void BurnPage(PdfDocument doc, PdfPage page, SKBitmap image)
    {
        double widthPt = page.Width.Point;
        double heightPt = page.Height.Point;

        var imageDict = new PdfDictionary(doc);
        imageDict.Elements["/Type"] = new PdfName("/XObject");
        imageDict.Elements["/Subtype"] = new PdfName("/Image");
        imageDict.Elements["/Width"] = new PdfInteger(image.Width);
        imageDict.Elements["/Height"] = new PdfInteger(image.Height);
        imageDict.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
        imageDict.Elements["/BitsPerComponent"] = new PdfInteger(8);
        imageDict.Elements["/Filter"] = new PdfName("/FlateDecode");
        imageDict.CreateStream(Deflate(ToRgbBytes(image)));
        doc.Internals.AddObject(imageDict);

        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/Im0"] = imageDict.Reference;
        page.Resources.Elements["/XObject"] = xobjects;

        // 旧内容（文本/绘图/旧资源）整体丢弃：定稿页从这一刻起只是这张图。
        var contentDict = new PdfDictionary(doc);
        contentDict.CreateStream(System.Text.Encoding.ASCII.GetBytes(System.FormattableString.Invariant(
            $"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q")));
        doc.Internals.AddObject(contentDict);
        page.Elements["/Contents"] = contentDict.Reference;
    }

    // --- 图像 helpers -------------------------------------------------------------

    /// <summary>把「显示方向」的光栅按页面 /Rotate 逆时针转回「页面方向」；0° 返回 null（无需转动）。</summary>
    private static SKBitmap? TurnToPageSpace(SKBitmap image, int rotate)
    {
        int steps = ((rotate % 360) + 360) / 90 % 4;
        if (steps == 0) return null;

        int width = image.Width;
        int height = image.Height;
        bool swap = steps % 2 == 1;
        var turned = new SKBitmap(new SKImageInfo(
            swap ? height : width, swap ? width : height, image.ColorType, image.AlphaType));
        using var canvas = new SKCanvas(turned);
        canvas.Clear(SKColors.Transparent);
        // 以图心为轴转：Skia 正角为顺时针（y 向下），负角即视觉上的逆时针。
        canvas.Translate(turned.Width / 2f, turned.Height / 2f);
        canvas.RotateDegrees(-90f * steps);
        canvas.Translate(-width / 2f, -height / 2f);
        canvas.DrawBitmap(image, 0, 0);
        canvas.Flush();
        return turned;
    }

    private static SKBitmap Load(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        return SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("标注图像解码失败");
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>BGRA 预乘像素 → RGB 直通字节（不透明图两者一致；半透明像素先解除预乘）。</summary>
    private static byte[] ToRgbBytes(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var rgb = new byte[width * height * 3];
        ReadOnlySpan<byte> pixels = bitmap.Bytes;
        int rowBytes = bitmap.RowBytes;

        int target = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int offset = row + x * 4;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                byte a = pixels[offset + 3];
                if (a is not (0 or 255))
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }

                rgb[target++] = r;
                rgb[target++] = g;
                rgb[target++] = b;
            }
        }

        return rgb;
    }

    /// <summary>zlib 包装的 deflate——PDF /FlateDecode 要的就是这个格式。</summary>
    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(
            output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}
