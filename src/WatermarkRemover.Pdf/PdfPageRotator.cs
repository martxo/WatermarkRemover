using System.Reflection;
using PdfSharpCore;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Edits one page of an existing PDF in place: turn it a quarter turn clockwise per call, or
/// remove it entirely. The preview's thumbnail buttons save through here immediately, so the
/// file on disk always matches what the user is looking at — there is no separate "save" step
/// to forget.
/// </summary>
/// <remarks>
/// Every edit is written to a side file first and swapped over the original only after the save
/// succeeded, so an interrupted edit can never leave a truncated PDF behind.
/// </remarks>
public static class PdfPageRotator
{
    /// <summary>Current /Rotate plus a quarter turn, always landing on 0 / 90 / 180 / 270.</summary>
    public static int NextClockwiseRotation(int currentRotation) => (currentRotation % 360 + 90) % 360;

    /// <param name="pdfPath">The file to modify in place.</param>
    /// <param name="pageIndex">Zero-based page index; out-of-range throws before anything is written.</param>
    public static void RotateClockwise(string pdfPath, int pageIndex) =>
        EditInPlace(pdfPath, pageIndex, static (_, page) => page.Rotate = NextClockwiseRotation(page.Rotate));

    /// <summary>
    /// Turns a page by the given number of clockwise quarter turns in one in-place edit — the
    /// auto-orientation path needs 2 or 3 turns at once, and four single saves per page would be
    /// wasteful (and racy against a concurrent preview reload).
    /// </summary>
    public static void RotateBy(string pdfPath, int pageIndex, int quarterTurns) =>
        EditDocument(pdfPath, doc =>
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"页码超出范围：第 {pageIndex + 1} 页（共 {doc.PageCount} 页）。");
            doc.Pages[pageIndex].Rotate = Turned(doc.Pages[pageIndex].Rotate, quarterTurns);
        });

    /// <summary>
    /// Turns every page of the document in one save — a duplex scan whose back sides came out
    /// upside down is fixed with a single file rewrite, not one per page.
    /// </summary>
    public static void RotateAll(string pdfPath, int quarterTurns) =>
        EditDocument(pdfPath, doc =>
        {
            foreach (var page in doc.Pages)
                page.Rotate = Turned(page.Rotate, quarterTurns);
        });

    private static int Turned(int currentRotation, int quarterTurns) =>
        ((currentRotation % 360) + 360 + 90 * (quarterTurns % 4)) % 360;

    /// <summary>
    /// Removes one page from the document, shifting later pages up one index. The last remaining
    /// page cannot be deleted: a zero-page PDF is not a valid document and readers cannot open it.
    /// </summary>
    /// <param name="pdfPath">The file to modify in place.</param>
    /// <param name="pageIndex">Zero-based page index; out-of-range throws before anything is written.</param>
    public static void DeletePage(string pdfPath, int pageIndex) =>
        EditInPlace(pdfPath, pageIndex, static (doc, page) =>
        {
            if (doc.PageCount <= 1)
                throw new InvalidOperationException("PDF 至少要保留一页，不能删除最后一页。");
            doc.Pages.Remove(page);
        });

    /// <summary>
    /// Removes several pages in one in-place save — the blank-page cleaner may drop any number
    /// of pages, and one rewrite per page would be wasteful and racy. The document must keep at
    /// least one page (callers trim the list; this guard backstops it).
    /// </summary>
    /// <returns>How many pages were actually removed.</returns>
    public static int DeletePages(string pdfPath, IReadOnlyList<int> pageIndices)
    {
        if (pageIndices.Count == 0) return 0;

        int removed = 0;
        EditDocument(pdfPath, doc =>
        {
            if (doc.PageCount - pageIndices.Count < 1)
                throw new InvalidOperationException("PDF 至少要保留一页，不能把所有页都删掉。");

            // Removing from the tail keeps the earlier indexes valid while they go.
            foreach (int index in pageIndices.OrderByDescending(i => i))
            {
                if (index < 0 || index >= doc.PageCount) continue;
                doc.Pages.Remove(doc.Pages[index]);
                removed++;
            }
        });
        return removed;
    }

    private static void EditInPlace(string pdfPath, int pageIndex, Action<PdfDocument, PdfPage> edit) =>
        EditDocument(pdfPath, doc =>
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"页码超出范围：第 {pageIndex + 1} 页（共 {doc.PageCount} 页）。");
            edit(doc, doc.Pages[pageIndex]);
        });

    private static void EditDocument(string pdfPath, Action<PdfDocument> edit)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(pdfPath));

        string tempPath = pdfPath + ".edit.tmp";
        try
        {
            PdfDocument? doc = null;
            try
            {
                doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify);
                DisableForeignPageOrientationCorrection(doc);
                edit(doc);
                doc.Save(tempPath);
            }
            finally
            {
                (doc as IDisposable)?.Dispose();
            }

            // Swap only now that the side file is complete and the original is released.
            File.Move(tempPath, pdfPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // A leftover side file is harmless; the PDF itself is already intact.
            }
        }
    }

    /// <summary>
    /// PdfSharpCore flags every page whose explicit /Rotate is 90/270 as "landscape" on open,
    /// and its WriteObject then swaps that page's MediaBox width/height whenever the page is
    /// serialized with an even rotation (0/180) — a correction meant for pages the library
    /// created itself. On foreign pages it is plain corruption: the content stream is copied
    /// unchanged, so the swapped box clips everything that now sits outside it (a portrait
    /// scan loses its top third). Rotating here is metadata-only, and /Rotate is interpreted
    /// by the viewer per the PDF spec — no library-side box compensation is wanted. Reset the
    /// flag so WriteObject never touches the MediaBox of any page.
    /// </summary>
    private static void DisableForeignPageOrientationCorrection(PdfDocument doc)
    {
        var orientationField = typeof(PdfPage).GetField("_orientation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (orientationField is null)
            return; // a future PdfSharpCore may rename the field; the swap guard simply degrades

        foreach (var page in doc.Pages)
            orientationField.SetValue(page, PageOrientation.Portrait);
    }
}
