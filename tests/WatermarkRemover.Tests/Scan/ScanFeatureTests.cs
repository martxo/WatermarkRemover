using System.Text;
using Docnet.Core;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Core.Pipeline;
using WatermarkRemover.Scan.Engine;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.Scan;

public class ScanFileNameTests
{
    [Fact]
    public void Uses_the_timestamp_format()
    {
        var stamp = new DateTime(2026, 10, 18, 15, 42, 30);
        Assert.Equal("扫描20261018-154230.pdf", ScanFileName.Create(stamp));
    }

    [Fact]
    public void A_name_collision_gets_a_counter_instead_of_overwriting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wm-scanname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string name = ScanFileName.Create(new DateTime(2026, 10, 18, 15, 42, 30));
            File.WriteAllBytes(Path.Combine(dir, name), Array.Empty<byte>());

            string second = OutputNameGenerator.FindAvailablePath(dir, name);

            Assert.Equal("扫描20261018-154230 (2).pdf", Path.GetFileName(second));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class PageOrderNormalizerTests
{
    private static RasterImage Page(int marker)
    {
        var pixels = new byte[4 * 4 * 4];
        pixels[0] = (byte)marker;
        return new RasterImage(4, 4, pixels);
    }

    private static ScanSettings Settings(DuplexMode duplex, FeedMode feed = FeedMode.Adf) =>
        new() { Duplex = duplex, Feed = feed };

    private static ScanSettings ManualFlipSettings(DuplexMode duplex, FeedMode feed = FeedMode.Adf) =>
        new() { Duplex = duplex, Feed = feed, DuplexFeedOrder = DuplexFeedOrder.FrontsThenReversedRears };

    [Fact]
    public void Simplex_pages_keep_their_order()
    {
        var raw = new[] { Page(1), Page(2), Page(3) };

        var ordered = PageOrderNormalizer.Normalize(raw, Settings(DuplexMode.Off));

        Assert.Equal(3, ordered.Count);
        Assert.Same(raw[0], ordered[0]);
        Assert.Same(raw[2], ordered[2]);
    }

    [Fact]
    public void Device_duplex_interleaved_output_is_never_reordered()
    {
        // Regression (2026-10-19): Brother ADK duplex walks each sheet once and transfers both
        // sides back to back — sheet1 front, sheet1 back, sheet2 front, sheet2 back. That is
        // already reading order; the old unconditional interleave shredded real duplex jobs
        // (sheet 1 came out as front+last-rear pairs).
        var raw = new[] { Page(11), Page(12), Page(21), Page(22) };

        Assert.Same(raw[0], PageOrderNormalizer.Normalize(raw, Settings(DuplexMode.LongEdge))[0]);
        Assert.Equal(new[] { 11, 12, 21, 22 },
            PageOrderNormalizer.Normalize(raw, Settings(DuplexMode.LongEdge)).Select(p => (int)p.Bgra[0]).ToArray());
        Assert.Equal(new[] { 11, 12, 21, 22 },
            PageOrderNormalizer.Normalize(raw, Settings(DuplexMode.ShortEdge)).Select(p => (int)p.Bgra[0]).ToArray());
    }

    [Fact]
    public void Manual_flip_batches_are_interleaved_back_first_in_reverse()
    {
        // Flipping the stack re-feeds every rear in reverse: sheet1 front, sheet2 front,
        // sheet2 back, sheet1 back. Interleaving restores reading order.
        var raw = new[] { Page(11), Page(21), Page(22), Page(12) };

        var ordered = PageOrderNormalizer.Normalize(raw, ManualFlipSettings(DuplexMode.LongEdge));

        Assert.Equal(4, ordered.Count);
        Assert.Same(raw[0], ordered[0]); // sheet 1 front
        Assert.Same(raw[3], ordered[1]); // sheet 1 back
        Assert.Same(raw[1], ordered[2]); // sheet 2 front
        Assert.Same(raw[2], ordered[3]); // sheet 2 back
    }

    [Fact]
    public void Short_edge_manual_flip_uses_the_same_page_order()
    {
        var raw = new[] { Page(11), Page(21), Page(22), Page(12) };

        var ordered = PageOrderNormalizer.Normalize(raw, ManualFlipSettings(DuplexMode.ShortEdge));

        Assert.Equal(4, ordered.Count);
        Assert.Same(raw[3], ordered[1]);
    }

    [Fact]
    public void An_odd_page_count_is_left_untouched()
    {
        var raw = new[] { Page(1), Page(2), Page(3) };

        var ordered = PageOrderNormalizer.Normalize(raw, ManualFlipSettings(DuplexMode.LongEdge));

        Assert.Same(raw[2], ordered[2]);
    }

    [Fact]
    public void Flatbed_runs_are_left_untouched()
    {
        var raw = new[] { Page(1), Page(2) };

        var ordered = PageOrderNormalizer.Normalize(raw, Settings(DuplexMode.LongEdge, FeedMode.Flatbed));

        Assert.Same(raw[0], ordered[0]);
        Assert.Same(raw[1], ordered[1]);
    }

    [Fact]
    public void ClassifyPage_marks_every_second_page_as_back_side()
    {
        var (sheet, back) = PageOrderNormalizer.ClassifyPage(0, DuplexMode.LongEdge);
        Assert.Equal(0, sheet);
        Assert.False(back);

        (sheet, back) = PageOrderNormalizer.ClassifyPage(1, DuplexMode.LongEdge);
        Assert.Equal(0, sheet);
        Assert.True(back);

        (sheet, back) = PageOrderNormalizer.ClassifyPage(3, DuplexMode.ShortEdge);
        Assert.Equal(1, sheet);
        Assert.True(back);

        (sheet, back) = PageOrderNormalizer.ClassifyPage(2, DuplexMode.Off);
        Assert.Equal(2, sheet);
        Assert.False(back);
    }
}

public class ScanPdfAssemblyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-scanpdf-" + Guid.NewGuid().ToString("N"));

    public ScanPdfAssemblyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Builds_a_readable_two_page_pdf_from_ordered_pages()
    {
        var pages = new List<ScannedPage>();
        for (int i = 0; i < 2; i++)
        {
            var pixels = new byte[40 * 30 * 4];
            new Random(i + 1).NextBytes(pixels);
            pages.Add(new ScannedPage
            {
                Image = new RasterImage(40, 30, pixels),
                Index = i,
                SheetIndex = i / 2,
                IsBackSide = i % 2 == 1,
            });
        }

        string path = Path.Combine(_dir, "扫描20261018-154230.pdf");
        var document = new ScanDocument
        {
            Pages = pages,
            Settings = new ScanSettings { Duplex = DuplexMode.LongEdge },
            BaseName = "扫描20261018-154230",
        };
        byte[] pdf = PdfDocumentBuilder.Build(
            document,
            new PdfBuildOptions { HighCompression = true },
            new ScanImageEncoder());
        File.WriteAllBytes(path, pdf);

        Assert.True(pdf.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));

        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var reader = lib.GetDocReader(path, new Docnet.Core.Models.PageDimensions(1080));
        Assert.Equal(2, reader.GetPageCount());
    }
}
