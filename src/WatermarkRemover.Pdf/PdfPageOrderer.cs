using PdfSharp.Pdf;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Changes the order of pages in one in-place save — the preview's drag-and-drop reorder lifts a
/// page out of its slot and puts it back at the target slot, leaving every page's content bytes
/// untouched. Uses the same side-file swap as every other page edit, so an interrupted write can
/// never leave a truncated PDF behind.
/// </summary>
public static class PdfPageOrderer
{
    /// <summary>
    /// Moves the page at <paramref name="fromIndex"/> so it ends up at <paramref name="toIndex"/>
    /// in the resulting document. Both indexes refer to the current document; <c>toIndex</c> is
    /// the slot the page occupies afterwards (the pages in between shift by one).
    /// </summary>
    /// <returns>The file holding the result — the same path, or a "原名 (已重排).pdf" copy when a
    /// viewer holds the original — or null when both indexes are the same (nothing to write).</returns>
    public static string? MovePage(string pdfPath, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return null;

        return PdfDocumentEditor.EditDocument(pdfPath, doc =>
        {
            if (fromIndex < 0 || fromIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(fromIndex),
                    $"页码超出范围：第 {fromIndex + 1} 页（共 {doc.PageCount} 页）。");
            if (toIndex < 0 || toIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(toIndex),
                    $"目标位置超出范围：第 {toIndex + 1} 位（共 {doc.PageCount} 页）。");

            // PDFsharp 6.x keeps every page in the cross-reference table while it is in the page
            // tree; a remove + insert of the same object throws "Object already in table". Its
            // dedicated MovePage keeps the object identity and only rewrites the page order.
            doc.Pages.MovePage(fromIndex, toIndex);
        }, keepCopyOnLock: true, fallbackSuffix: "已重排");
    }
}
