using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Splices every page of one PDF into another at a chosen position, in a single in-place save —
/// the preview's gap button inserts freshly scanned pages between existing ones without
/// rebuilding (and re-encoding) the document. The same side-file swap as every other page edit
/// protects the original: it is replaced only after a complete save.
/// </summary>
public static class PdfPageInserter
{
    /// <summary>Inserts every page of <paramref name="sourcePdfPath"/> right after the given
    /// zero-based page of the target and returns how many pages were added.</summary>
    public static int InsertAfter(string targetPdfPath, int insertAfterIndex, string sourcePdfPath) =>
        Insert(targetPdfPath, insertAfterIndex + 1, sourcePdfPath);

    /// <summary>Inserts every page of the source PDF at the given zero-based position of the
    /// target (PageCount = append at the end) and returns how many pages were added.</summary>
    public static int Insert(string targetPdfPath, int position, string sourcePdfPath)
    {
        if (string.IsNullOrWhiteSpace(targetPdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(targetPdfPath));
        if (string.IsNullOrWhiteSpace(sourcePdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(sourcePdfPath));
        if (position < 0)
            throw new ArgumentOutOfRangeException(nameof(position),
                $"插入位置不能为负：{position}。");

        int inserted = 0;
        PdfDocumentEditor.EditDocument(targetPdfPath, doc =>
        {
            if (position > doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"插入位置超出范围：第 {position + 1} 页之前（共 {doc.PageCount} 页）。");

            // Import mode marks the source as a pure page donor; InsertRange copies the whole
            // page tree into the target at the slot, keeping the source order.
            using PdfDocument source = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
            doc.Pages.InsertRange(position, source);
            inserted = source.PageCount;
        });
        return inserted;
    }
}
