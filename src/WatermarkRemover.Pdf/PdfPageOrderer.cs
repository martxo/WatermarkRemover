using PdfSharpCore.Pdf;

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
    /// Returns false when the two indexes are the same, i.e. there is nothing to write.
    /// </summary>
    public static bool MovePage(string pdfPath, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return false;

        PdfDocumentEditor.EditDocument(pdfPath, doc =>
        {
            if (fromIndex < 0 || fromIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(fromIndex),
                    $"页码超出范围：第 {fromIndex + 1} 页（共 {doc.PageCount} 页）。");
            if (toIndex < 0 || toIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(toIndex),
                    $"目标位置超出范围：第 {toIndex + 1} 位（共 {doc.PageCount} 页）。");

            PdfPage page = doc.Pages[fromIndex];
            doc.Pages.Remove(page);
            doc.Pages.Insert(toIndex, page);
        });
        return true;
    }
}
