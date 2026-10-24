using System.Text;
using Docnet.Core;
using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Engine;
using WatermarkRemover.Scan.Pdf;
using WatermarkRemover.Scan.Pdf.Internal;

namespace WatermarkRemover.Tests.Scan;

/// <summary>
/// The official-style bilevel PDF path: engine 黑白 frames must be embedded as raw CCITT G4
/// (lossless, tiny), colour frames must keep the regular encoder, and fax-encoded pages must
/// render back pixel-faithful through a real PDF engine (pdfium).
/// </summary>
public class BilevelPdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bilevel-pdf-{Guid.NewGuid():N}");

    public BilevelPdfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp best effort */ }
    }

    private static RasterImage BilevelFrame(int width, int height, Func<int, int, bool> isWhite)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var v = (byte)(isWhite(x, y) ? 255 : 0);
                bgra[o] = v;
                bgra[o + 1] = v;
                bgra[o + 2] = v;
                bgra[o + 3] = 255;
            }
        }

        return new RasterImage(width, height, bgra);
    }

    private static ScanDocument Document(params RasterImage[] pages) => new()
    {
        Pages = pages.Select((image, i) => new ScannedPage { Image = image, Index = i, SheetIndex = i }).ToList(),
        Settings = new ScanSettings { Resolution = ScanResolution.Dpi300, Color = ColorMode.BlackWhite },
        BaseName = "扫描黑白测试",
    };

    [Fact]
    public void TryPackBilevel_AcceptsPureBilevelAndRejectsGrayOrTranslucent()
    {
        var bilevel = BilevelFrame(16, 8, (x, y) => (x + y) % 2 == 0);
        Assert.NotNull(CcittG4Image.TryPackBilevel(bilevel));

        var gray = BilevelFrame(16, 8, (x, y) => (x + y) % 2 == 0);
        gray.Bgra[0] = 128; // one gray pixel disqualifies the frame
        Assert.Null(CcittG4Image.TryPackBilevel(gray));

        var translucent = BilevelFrame(16, 8, (x, y) => (x + y) % 2 == 0);
        translucent.Bgra[3] = 200; // alpha != 255 disqualifies too
        Assert.Null(CcittG4Image.TryPackBilevel(translucent));
    }

    [Fact]
    public void CompressG4_BeatsRawRowsOnTextLikePattern()
    {
        var packed = CcittG4Image.TryPackBilevel(BilevelFrame(620, 400, (x, y) => (x / 13 + y / 7) % 3 != 0))!;
        var g4 = CcittG4Image.CompressG4(packed, 620, 400);
        Assert.NotEmpty(g4);
        Assert.True(g4.Length < packed.Length, $"G4 {g4.Length}B should beat raw rows {packed.Length}B");
    }

    [Fact]
    public void Build_BilevelPagesEmbedCcittFaxDecode()
    {
        var frame = BilevelFrame(120, 90, (x, y) => (x / 7 + y / 5) % 3 != 0);
        var bytes = PdfDocumentBuilder.Build(Document(frame), new PdfBuildOptions { HighCompression = true }, new ScanImageEncoder());

        Assert.Contains("/CCITTFaxDecode", Encoding.ASCII.GetString(bytes));
        Assert.DoesNotContain("/DCTDecode", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Build_PreEncodedPagesKeepsBilevelAndColourPayloads()
    {
        var bilevel = BilevelFrame(120, 90, (x, y) => (x + y) % 4 != 0);
        var colour = new RasterImage(120, 90, Enumerable.Repeat<byte>(200, 120 * 90 * 4).ToArray());
        var document = Document(bilevel, colour);
        var options = new PdfBuildOptions { HighCompression = true };
        var encoder = new ScanImageEncoder();
        double dpi = PdfDocumentBuilder.ResolveDpi(document.Settings);

        var encoded = document.Pages
            .Select(page => PdfDocumentBuilder.EncodePage(page, options, encoder, dpi))
            .ToList();
        byte[] bytes = PdfDocumentBuilder.Build(document, options, encoded);

        var raw = Encoding.ASCII.GetString(bytes);
        Assert.Contains("/CCITTFaxDecode", raw);
        Assert.Contains("/DCTDecode", raw);

        string path = Path.Combine(_dir, "pre-encoded.pdf");
        File.WriteAllBytes(path, bytes);
        using var reader = DocLib.Instance.GetDocReader(path, new Docnet.Core.Models.PageDimensions(300.0 / 72.0));
        Assert.Equal(2, reader.GetPageCount());
    }

    [Fact]
    public void Build_BilevelAndColourPagesMixPerFrame()
    {
        var bilevel = BilevelFrame(120, 90, (x, y) => (x + y) % 4 != 0);
        var colour = new RasterImage(120, 90, Enumerable.Repeat<byte>(200, 120 * 90 * 4).ToArray());
        var bytes = PdfDocumentBuilder.Build(Document(bilevel, colour), new PdfBuildOptions { HighCompression = true }, new ScanImageEncoder());

        var raw = Encoding.ASCII.GetString(bytes);
        Assert.Contains("/CCITTFaxDecode", raw);
        Assert.Contains("/DCTDecode", raw);

        var path = Path.Combine(_dir, "mixed.pdf");
        File.WriteAllBytes(path, bytes);
        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var reader = lib.GetDocReader(path, new Docnet.Core.Models.PageDimensions(300.0 / 72.0));
        Assert.Equal(2, reader.GetPageCount());
    }

    [Fact]
    public void Build_BilevelPageRendersSameAsLosslessReference()
    {
        // Render-geometry differences (pdfium pt rounding) make a direct compare against the
        // source bitmap flaky, so instead compare against the SAME frame embedded through the
        // lossless PNG path — identical page geometry, so both renders must agree everywhere.
        const int width = 120, height = 90;
        var frame = BilevelFrame(width, height, (x, y) => (x / 7 + y / 5) % 3 != 0);

        var g4Bytes = PdfDocumentBuilder.Build(
            Document(frame), new PdfBuildOptions { HighCompression = true }, new ScanImageEncoder());
        Assert.Contains("/CCITTFaxDecode", Encoding.ASCII.GetString(g4Bytes));

        var grayFrame = BilevelFrame(width, height, (x, y) => (x / 7 + y / 5) % 3 != 0);
        grayFrame.Bgra[4] = 100; // single gray pixel forces the regular (non-fax) path
        var referenceBytes = PdfDocumentBuilder.Build(
            Document(grayFrame), new PdfBuildOptions { HighCompression = false }, new ScanImageEncoder());

        var g4Path = Path.Combine(_dir, "g4.pdf");
        var refPath = Path.Combine(_dir, "reference.pdf");
        File.WriteAllBytes(g4Path, g4Bytes);
        File.WriteAllBytes(refPath, referenceBytes);

        var lib = DocLib.Instance; // shared singleton — never dispose it
        using (var g4Reader = lib.GetDocReader(g4Path, new Docnet.Core.Models.PageDimensions(300.0 / 72.0)))
        using (var refReader = lib.GetDocReader(refPath, new Docnet.Core.Models.PageDimensions(300.0 / 72.0)))
        {
            Assert.Equal(1, g4Reader.GetPageCount());
            Assert.Equal(1, refReader.GetPageCount());
            using var g4Page = g4Reader.GetPageReader(0);
            using var refPage = refReader.GetPageReader(0);
            Assert.Equal(refPage.GetPageWidth(), g4Page.GetPageWidth());
            Assert.Equal(refPage.GetPageHeight(), g4Page.GetPageHeight());

            var g4Pixels = g4Page.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover(255, 255, 255));
            var refPixels = refPage.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover(255, 255, 255));

            var total = g4Page.GetPageWidth() * g4Page.GetPageHeight();
            var diffs = 0;
            for (var i = 0; i < total * 4; i += 4)
            {
                if (Math.Abs(g4Pixels[i] - refPixels[i]) > 32) diffs++;
            }

            // The injected gray pixel (and its resampling halo) is the only expected difference.
            Assert.True(diffs <= total / 100, $"{diffs}/{total} pixels differ — fax stream likely corrupt");
        }
    }

    [Fact]
    public void Build_SearchableBilevelPageStillRendersImage()
    {
        var frame = BilevelFrame(120, 90, (x, y) => (x / 7 + y / 5) % 3 != 0);
        var words = new List<OcrWord> { new("测试", 10, 10, 40, 12, 0.9f) };
        var document = new ScanDocument
        {
            Pages = new List<ScannedPage>
            {
                new() { Image = frame, Index = 0, SheetIndex = 0, Words = words },
            },
            Settings = new ScanSettings { Resolution = ScanResolution.Dpi300, Color = ColorMode.BlackWhite },
            BaseName = "搜索层",
        };

        var bytes = PdfDocumentBuilder.Build(
            document, new PdfBuildOptions { HighCompression = true, Searchable = true }, new ScanImageEncoder());

        var path = Path.Combine(_dir, "searchable.pdf");
        File.WriteAllBytes(path, bytes);

        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var docReader = lib.GetDocReader(path, new Docnet.Core.Models.PageDimensions(300.0 / 72.0));
        using var pageReader = docReader.GetPageReader(0);
        var pixels = pageReader.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover(255, 255, 255));
        // Centre of the first white cell must stay white — the text layer must not smear it.
        Assert.True(pixels[(45 * pageReader.GetPageWidth() + 60) * 4] >= 128, "image was corrupted by the text layer");
    }
}
