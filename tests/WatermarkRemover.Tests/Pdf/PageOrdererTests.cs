using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.Pdf;

/// <summary>
/// Drag-and-drop reordering is a file-level promise: the moved page lands in the requested slot,
/// every other page keeps its relative order and its own content, and an invalid target leaves
/// the file exactly as it was. Pages are told apart by MediaBox width, which survives the save.
/// </summary>
public class PageOrdererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-order-" + Guid.NewGuid().ToString("N"));

    public PageOrdererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>One page per width (pt) — the width is the page's identity. The last page of
    /// each document is also turned 90°, so the test proves page properties travel with it.</summary>
    private string WritePdf(string name, params double[] widths)
    {
        string path = Path.Combine(_dir, name);
        using (var doc = new PdfDocument())
        {
            foreach (double width in widths)
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, width, 100));
                page.Rotate = 90;
            }
            doc.Save(path);
        }
        return path;
    }

    private static (double Width, int Rotate)[] PagesOf(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages.Cast<PdfPage>()
            .Select(p => (p.MediaBox.Width, p.Rotate))
            .ToArray();
    }

    [Fact]
    public void Moving_a_page_later_shifts_the_pages_between_it_left()
    {
        string pdf = WritePdf("later.pdf", 100, 110, 120, 130);

        Assert.Equal(pdf, PdfPageOrderer.MovePage(pdf, 0, 2)); // written in place, no copy

        Assert.Equal(new[] { 110.0, 120.0, 100.0, 130.0 }, PagesOf(pdf).Select(p => p.Width));
        Assert.Equal(90, PagesOf(pdf)[2].Rotate); // the page kept its own /Rotate
        Assert.False(File.Exists(pdf + ".edit.tmp"), "temporary file must not survive the swap");
    }

    [Fact]
    public void Moving_a_page_earlier_shifts_the_pages_between_it_right()
    {
        string pdf = WritePdf("earlier.pdf", 100, 110, 120, 130);

        Assert.Equal(pdf, PdfPageOrderer.MovePage(pdf, 3, 1));

        Assert.Equal(new[] { 100.0, 130.0, 110.0, 120.0 }, PagesOf(pdf).Select(p => p.Width));
    }

    [Fact]
    public void Moving_a_page_to_the_last_slot_appends_it()
    {
        // After the removal the document holds one page fewer, so the last slot is exactly the
        // append position: the reorder must accept it instead of rejecting the original index.
        string pdf = WritePdf("last.pdf", 100, 110, 120);

        Assert.Equal(pdf, PdfPageOrderer.MovePage(pdf, 0, 2));

        Assert.Equal(new[] { 110.0, 120.0, 100.0 }, PagesOf(pdf).Select(p => p.Width));
    }

    [Fact]
    public void An_out_of_range_target_fails_before_anything_is_written()
    {
        string pdf = WritePdf("invalid.pdf", 100, 110, 120);
        byte[] before = File.ReadAllBytes(pdf);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageOrderer.MovePage(pdf, 0, 3));

        Assert.Equal(before, File.ReadAllBytes(pdf));
        Assert.False(File.Exists(pdf + ".edit.tmp"));
    }

    [Fact]
    public void Dropping_a_page_on_its_own_slot_writes_nothing()
    {
        string pdf = WritePdf("same.pdf", 100, 110);
        byte[] before = File.ReadAllBytes(pdf);

        Assert.Null(PdfPageOrderer.MovePage(pdf, 1, 1));

        Assert.Equal(before, File.ReadAllBytes(pdf));
    }

    [Fact]
    public void A_locked_document_reorders_into_a_copy_beside_it()
    {
        string pdf = WritePdf("locked.pdf", 100, 110, 120);

        // A viewer holding the file for reading blocks the swap-over save.
        using var hold = new FileStream(pdf, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? result = PdfPageOrderer.MovePage(pdf, 0, 2);

        Assert.NotNull(result);
        Assert.False(string.Equals(pdf, result, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { 100.0, 110.0, 120.0 }, PagesOf(pdf).Select(p => p.Width));
        Assert.Equal(new[] { 110.0, 120.0, 100.0 }, PagesOf(result!).Select(p => p.Width));
    }
}
