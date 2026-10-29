using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

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

    /// <summary>
    /// Turns one page a quarter turn clockwise and returns the file holding the result: the same
    /// path, or a "原名 (已旋转).pdf" copy when a viewer holds the original.
    /// </summary>
    /// <param name="pdfPath">The file to modify.</param>
    /// <param name="pageIndex">Zero-based page index; out-of-range throws before anything is written.</param>
    public static string RotateClockwise(string pdfPath, int pageIndex) =>
        EditInPlace(pdfPath, pageIndex,
            static (_, page) => page.Rotate = NextClockwiseRotation(page.Rotate), "已旋转");

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

    /// <summary>
    /// Turns several pages, each by its own quarter-turn count, in ONE in-place save — the
    /// insert path probes every fresh page's orientation, then fixes them all with a single
    /// file rewrite instead of one rewrite per turned page. Zero-turn entries are skipped;
    /// every index is validated before anything is mutated, and the swap-over save means an
    /// exception can never leave a truncated file behind.
    /// </summary>
    public static void RotatePages(string pdfPath, IReadOnlyList<(int PageIndex, int QuarterTurns)> turns)
    {
        if (turns.Count == 0) return;
        EditDocument(pdfPath, doc =>
        {
            foreach (var (pageIndex, _) in turns)
            {
                if (pageIndex < 0 || pageIndex >= doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(turns),
                        $"页码超出范围：第 {pageIndex + 1} 页（共 {doc.PageCount} 页）。");
            }

            foreach (var (pageIndex, quarterTurns) in turns)
            {
                if (quarterTurns % 4 == 0) continue;
                doc.Pages[pageIndex].Rotate = Turned(doc.Pages[pageIndex].Rotate, quarterTurns);
            }
        });
    }

    private static int Turned(int currentRotation, int quarterTurns) =>
        ((currentRotation % 360) + 360 + 90 * (quarterTurns % 4)) % 360;

    /// <summary>
    /// Removes one page and returns the file holding the result: the same path, or a
    /// "原名 (已删除).pdf" copy when a viewer holds the original. Later pages shift up one index;
    /// the last remaining page cannot be deleted — a zero-page PDF cannot be opened.
    /// </summary>
    /// <param name="pdfPath">The file to modify.</param>
    /// <param name="pageIndex">Zero-based page index; out-of-range throws before anything is written.</param>
    public static string DeletePage(string pdfPath, int pageIndex) =>
        EditInPlace(pdfPath, pageIndex, static (doc, page) =>
        {
            if (doc.PageCount <= 1)
                throw new InvalidOperationException("PDF 至少要保留一页，不能删除最后一页。");
            doc.Pages.Remove(page);
        }, "已删除");

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

    private static string EditInPlace(
        string pdfPath, int pageIndex, Action<PdfDocument, PdfPage> edit, string copySuffix) =>
        PdfDocumentEditor.EditDocument(pdfPath, doc =>
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"页码超出范围：第 {pageIndex + 1} 页（共 {doc.PageCount} 页）。");
            edit(doc, doc.Pages[pageIndex]);
        }, keepCopyOnLock: true, fallbackSuffix: copySuffix);

    private static void EditDocument(string pdfPath, Action<PdfDocument> edit) =>
        PdfDocumentEditor.EditDocument(pdfPath, edit);
}
