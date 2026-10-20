using Docnet.Core;
using Docnet.Core.Models;
using WatermarkRemover.Pdf;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.Pdf;

/// <summary>
/// Page rotation is a file-level promise: what the preview shows after a rotate click must be what
/// the PDF on disk carries. These tests check both halves — the persisted /Rotate entry and the
/// orientation a PDFium-based renderer actually reports for that file.
/// </summary>
public class PageRotatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-rotate-" + Guid.NewGuid().ToString("N"));

    public PageRotatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>The fixture is 842×595 — landscape, so a quarter turn is unmistakable.</summary>
    private string WriteLandscapeFixture()
    {
        string path = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(path, PdfFixture.CamScannerLike().Build());
        return path;
    }

    [Fact]
    public void Next_clockwise_rotation_walks_around_the_quadrants()
    {
        Assert.Equal(90, PdfPageRotator.NextClockwiseRotation(0));
        Assert.Equal(180, PdfPageRotator.NextClockwiseRotation(90));
        Assert.Equal(270, PdfPageRotator.NextClockwiseRotation(180));
        Assert.Equal(0, PdfPageRotator.NextClockwiseRotation(270));
        // 360 is the same orientation as 0, so it also walks on to 90.
        Assert.Equal(90, PdfPageRotator.NextClockwiseRotation(360));
    }

    [Fact]
    public void Rotating_a_page_flips_the_rendered_orientation()
    {
        string path = WriteLandscapeFixture();
        var (beforeW, beforeH) = RenderSize(path);
        Assert.True(beforeW > beforeH, $"fixture should be landscape, got {beforeW}x{beforeH}");

        PdfPageRotator.RotateClockwise(path, 0);

        var (afterW, afterH) = RenderSize(path);
        Assert.True(afterW < afterH, $"page should be portrait after rotation, got {afterW}x{afterH}");
        Assert.Equal(beforeH, afterW);
        Assert.Equal(beforeW, afterH);
    }

    [Fact]
    public void The_saved_file_carries_Rotate_and_no_side_file_is_left_over()
    {
        string path = WriteLandscapeFixture();

        PdfPageRotator.RotateClockwise(path, 0);
        PdfPageRotator.RotateClockwise(path, 0);

        string text = File.ReadAllText(path);
        Assert.Contains("/Rotate 180", text);
        Assert.False(File.Exists(path + ".edit.tmp"), "temporary file must not survive the swap");
        Assert.Equal(1, ReadPageCount(path));
    }

    [Fact]
    public void An_out_of_range_page_fails_before_anything_is_written()
    {
        string path = WriteLandscapeFixture();
        string before = File.ReadAllText(path);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRotator.RotateClockwise(path, 5));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".edit.tmp"));
    }

    [Fact]
    public void Rotating_a_flagged_landscape_page_never_touches_the_MediaBox()
    {
        // The user's scan shape: a portrait MediaBox with an explicit /Rotate 90. PdfSharpCore
        // flags such pages "landscape" on open and its WriteObject swaps the box whenever the
        // page is saved with an even rotation — while the content stream is copied unchanged,
        // so the swap pushes a third of the portrait page outside the box and viewers clip it.
        // The rotator must walk the full 90→180→270→0→90 cycle leaving the box byte-identical.
        string path = Path.Combine(_dir, "flagged.pdf");
        using (var doc = new PdfSharpCore.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            page.MediaBox = new PdfSharpCore.Pdf.PdfRectangle(
                new PdfSharpCore.Drawing.XRect(0, 0, 578.16, 824.88));
            page.Rotate = 90;
            doc.Save(path);
        }
        string boxBefore = ExtractMediaBox(File.ReadAllText(path));

        PdfPageRotator.RotateClockwise(path, 0); // 90 → 180, the exact clipping trigger
        Assert.Equal(180, ReadRotate(path));
        Assert.Equal(boxBefore, ExtractMediaBox(File.ReadAllText(path)));

        PdfPageRotator.RotateClockwise(path, 0); // 180 → 270
        PdfPageRotator.RotateClockwise(path, 0); // 270 → 0
        PdfPageRotator.RotateClockwise(path, 0); // 0 → 90
        Assert.Equal(90, ReadRotate(path));
        Assert.Equal(boxBefore, ExtractMediaBox(File.ReadAllText(path)));
    }

    private static string ExtractMediaBox(string pdfText)
    {
        var match = System.Text.RegularExpressions.Regex.Match(pdfText, @"/MediaBox\s*\[[^\]]*\]");
        Assert.True(match.Success, "fixture must carry an explicit /MediaBox");
        return match.Value;
    }

    private static int ReadRotate(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), @"/Rotate\s+(\d+)");
        Assert.True(match.Success, "rotated page must carry an explicit /Rotate");
        return int.Parse(match.Groups[1].Value);
    }

    // --- DeletePage ---

    /// <summary>A blank multi-page PDF written with PDFsharp itself.</summary>
    private string WriteMultiPageFixture(int pageCount)
    {
        string path = Path.Combine(_dir, $"doc{pageCount}.pdf");
        using (var doc = new PdfSharpCore.Pdf.PdfDocument())
        {
            for (int i = 0; i < pageCount; i++) doc.AddPage();
            doc.Save(path);
        }
        return path;
    }

    [Fact]
    public void Deleting_a_page_removes_it_and_shifts_the_rest()
    {
        string path = WriteMultiPageFixture(3);

        PdfPageRotator.DeletePage(path, 1); // middle page

        Assert.Equal(2, ReadPageCount(path));
        Assert.False(File.Exists(path + ".edit.tmp"), "temporary file must not survive the swap");
    }

    [Fact]
    public void Deleting_the_only_page_is_refused_without_touching_the_file()
    {
        string path = WriteLandscapeFixture();
        string before = File.ReadAllText(path);

        // A zero-page PDF is not a valid document, so the delete must never land on disk.
        Assert.Throws<InvalidOperationException>(() => PdfPageRotator.DeletePage(path, 0));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(1, ReadPageCount(path));
        Assert.False(File.Exists(path + ".edit.tmp"));
    }

    [Fact]
    public void An_out_of_range_delete_fails_before_anything_is_written()
    {
        string path = WriteMultiPageFixture(2);
        string before = File.ReadAllText(path);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRotator.DeletePage(path, 2));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".edit.tmp"));
    }

    // --- RotatePages (batch) ---

    [Fact]
    public void Rotating_several_pages_applies_each_turn_in_one_save()
    {
        string path = WriteMultiPageFixture(3);

        PdfPageRotator.RotatePages(path, new[] { (PageIndex: 0, QuarterTurns: 1), (PageIndex: 1, QuarterTurns: 0), (PageIndex: 2, QuarterTurns: 3) });

        Assert.Equal(3, ReadPageCount(path));
        var (w0, h0) = RenderSizeAt(path, 0);
        Assert.True(w0 > h0, $"page 1 should be landscape after +90°, got {w0}x{h0}");
        var (w1, h1) = RenderSizeAt(path, 1);
        Assert.True(w1 < h1, "page 2 carried 0 turns and must stay portrait");
        var (w2, h2) = RenderSizeAt(path, 2);
        Assert.True(w2 > h2, $"page 3 should be landscape after +270°, got {w2}x{h2}");
        Assert.False(File.Exists(path + ".edit.tmp"), "temporary file must not survive the swap");
    }

    [Fact]
    public void Batch_rotate_validates_every_index_before_writing_anything()
    {
        string path = WriteMultiPageFixture(2);
        string before = File.ReadAllText(path);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageRotator.RotatePages(path, new[] { (PageIndex: 0, QuarterTurns: 1), (PageIndex: 5, QuarterTurns: 1) }));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".edit.tmp"));
    }

    /// <summary>Size as a PDFium-based renderer sees it — the same channel the preview binds to.</summary>
    private static (int Width, int Height) RenderSizeAt(string path, int pageIndex)
    {
        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var reader = lib.GetDocReader(path, new PageDimensions(0.25));
        var page = reader.GetPageReader(pageIndex);
        return (page.GetPageWidth(), page.GetPageHeight());
    }

    /// <summary>Size as a PDFium-based renderer sees it — the same channel the preview binds to.</summary>
    private static (int Width, int Height) RenderSize(string path)
    {
        var lib = DocLib.Instance; // shared singleton — never dispose it
        using var reader = lib.GetDocReader(path, new PageDimensions(0.25));
        var page = reader.GetPageReader(0);
        return (page.GetPageWidth(), page.GetPageHeight());
    }

    private static int ReadPageCount(string path)
    {
        var lib = DocLib.Instance;
        using var reader = lib.GetDocReader(path, new PageDimensions(0.25));
        return reader.GetPageCount();
    }
}
