using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.Pdf;

/// <summary>
/// Page insertion is a file-level promise: the source document's pages land between the target's
/// existing pages, in order, in one in-place save — and a refused insert leaves the file exactly
/// as it was. Pages are told apart by MediaBox width, a marker that survives the import.
/// </summary>
public class PageInserterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-insert-" + Guid.NewGuid().ToString("N"));

    public PageInserterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>One page per width (pt) — the widths are the page order's fingerprints.</summary>
    private string WritePdf(string name, params double[] widths)
    {
        string path = Path.Combine(_dir, name);
        using (var doc = new PdfDocument())
        {
            foreach (double width in widths)
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, width, 100));
            }
            doc.Save(path);
        }
        return path;
    }

    private static double[] WidthsOf(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages.Cast<PdfPage>().Select(p => p.MediaBox.Width).ToArray();
    }

    [Fact]
    public void Inserting_after_a_page_lands_the_source_pages_in_order_between_the_existing_ones()
    {
        string target = WritePdf("target.pdf", 100, 110, 120);
        string source = WritePdf("source.pdf", 210, 220);

        int added = PdfPageInserter.InsertAfter(target, 0, source);

        Assert.Equal(2, added);
        Assert.Equal(new double[] { 100, 210, 220, 110, 120 }, WidthsOf(target));
        Assert.False(File.Exists(target + ".edit.tmp"), "temporary file must not survive the swap");
    }

    [Fact]
    public void Inserting_after_the_last_page_appends()
    {
        string target = WritePdf("target.pdf", 100, 110);
        string source = WritePdf("source.pdf", 210);

        PdfPageInserter.InsertAfter(target, 1, source);

        Assert.Equal(new double[] { 100, 110, 210 }, WidthsOf(target));
    }

    [Fact]
    public void Appending_lands_the_source_pages_after_the_last_page()
    {
        string target = WritePdf("target.pdf", 100, 110);
        string source = WritePdf("source.pdf", 210, 220);

        int added = PdfPageInserter.Append(target, source);

        Assert.Equal(2, added);
        Assert.Equal(new double[] { 100, 110, 210, 220 }, WidthsOf(target));
        Assert.False(File.Exists(target + ".edit.tmp"), "temporary file must not survive the swap");
    }

    [Fact]
    public void An_out_of_range_position_fails_before_anything_is_written()
    {
        string target = WritePdf("target.pdf", 100, 110, 120);
        string source = WritePdf("source.pdf", 210);
        byte[] before = File.ReadAllBytes(target);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageInserter.InsertAfter(target, 5, source));

        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Equal(3, WidthsOf(target).Length);
        Assert.False(File.Exists(target + ".edit.tmp"));
    }

    [Fact]
    public void A_locked_target_keeps_the_edited_copy_beside_it()
    {
        string target = WritePdf("target.pdf", 100, 110);
        string source = WritePdf("source.pdf", 210);

        // A viewer holding the file for reading (the WPS case) blocks the rename, not the read.
        using (var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PdfInsertResult result = PdfPageInserter.AppendDetailed(target, source);

            Assert.True(result.SavedAsCopy);
            Assert.False(string.Equals(target, result.PdfPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(result.PdfPath));
            Assert.Equal(new double[] { 100, 110 }, WidthsOf(target));
            Assert.Equal(new double[] { 100, 110, 210 }, WidthsOf(result.PdfPath));
        }

        Assert.False(File.Exists(target + ".edit.tmp"), "temporary file must not survive");
        Assert.False(File.Exists(target + ".edit.src.tmp"), "source copy must not survive");
    }

    [Fact]
    public void The_target_pages_keep_their_box_through_the_save()
    {
        // The MediaBox swap guard must hold on the insert path too: an inserted document's
        // original pages go through WriteObject just like a rotation's do.
        string target = WritePdf("target.pdf", 100, 110);
        string source = WritePdf("source.pdf", 210);

        PdfPageInserter.InsertAfter(target, 0, source);

        Assert.Equal(new double[] { 100, 210, 110 }, WidthsOf(target));
    }
}
