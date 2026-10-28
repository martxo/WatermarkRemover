using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace WatermarkRemover.Pdf;

/// <summary>Outcome of one insertion: page count, final path, and whether a locked original forced a copy.</summary>
public sealed record PdfInsertResult(int AddedPages, string PdfPath, bool SavedAsCopy);

/// <summary>
/// Splices every page of one PDF into another at a chosen position, in a single side-file save —
/// the preview's gap button inserts freshly scanned pages between existing ones without
/// rebuilding (and re-encoding) the document. When the target is open in WPS, the edited copy is
/// kept beside it as "原名 (已插入).pdf" instead of failing.
/// </summary>
public static class PdfPageInserter
{
    /// <summary>Inserts every page of <paramref name="sourcePdfPath"/> right after the given
    /// zero-based page of the target and returns how many pages were added.</summary>
    public static int InsertAfter(string targetPdfPath, int insertAfterIndex, string sourcePdfPath) =>
        InsertAfterDetailed(targetPdfPath, insertAfterIndex, sourcePdfPath).AddedPages;

    /// <summary>Detailed form of <see cref="InsertAfter"/>: also returns the final path.</summary>
    public static PdfInsertResult InsertAfterDetailed(
        string targetPdfPath, int insertAfterIndex, string sourcePdfPath) =>
        InsertDetailed(targetPdfPath, insertAfterIndex + 1, sourcePdfPath);

    /// <summary>Appends every page of <paramref name="sourcePdfPath"/> to the end of the target
    /// and returns how many pages were added — the continuation of an interrupted scan lands on
    /// the same document instead of becoming a second file.</summary>
    public static int Append(string targetPdfPath, string sourcePdfPath) =>
        AppendDetailed(targetPdfPath, sourcePdfPath).AddedPages;

    /// <summary>Detailed form of <see cref="Append"/>: also returns the final path.</summary>
    public static PdfInsertResult AppendDetailed(string targetPdfPath, string sourcePdfPath)
    {
        ValidatePaths(targetPdfPath, sourcePdfPath);

        int inserted = 0;
        string resultPath = PdfDocumentEditor.EditDocument(
            targetPdfPath,
            doc =>
            {
                using PdfDocument source = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
                doc.Pages.InsertRange(doc.PageCount, source);
                inserted = source.PageCount;
            },
            keepCopyOnLock: true,
            fallbackSuffix: "已追加");

        return CreateResult(targetPdfPath, resultPath, inserted);
    }

    /// <summary>Inserts every page of the source PDF at the given zero-based position of the
    /// target (PageCount = append at the end) and returns how many pages were added.</summary>
    public static int Insert(string targetPdfPath, int position, string sourcePdfPath) =>
        InsertDetailed(targetPdfPath, position, sourcePdfPath).AddedPages;

    /// <summary>Detailed form of <see cref="Insert"/>: also returns the final path.</summary>
    public static PdfInsertResult InsertDetailed(string targetPdfPath, int position, string sourcePdfPath)
    {
        ValidatePaths(targetPdfPath, sourcePdfPath);
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position),
                $"插入位置不能为负：{position}。");
        }

        int inserted = 0;
        string resultPath = PdfDocumentEditor.EditDocument(
            targetPdfPath,
            doc =>
            {
                if (position > doc.PageCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(position),
                        $"插入位置超出范围：第 {position + 1} 页之前（共 {doc.PageCount} 页）。");
                }

                // Import mode marks the source as a pure page donor; InsertRange copies the whole
                // page tree into the target at the slot, keeping the source order.
                using PdfDocument source = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
                doc.Pages.InsertRange(position, source);
                inserted = source.PageCount;
            },
            keepCopyOnLock: true,
            fallbackSuffix: "已插入");

        return CreateResult(targetPdfPath, resultPath, inserted);
    }

    private static PdfInsertResult CreateResult(string targetPath, string resultPath, int inserted) =>
        new(inserted, resultPath,
            !string.Equals(resultPath, targetPath, StringComparison.OrdinalIgnoreCase));

    private static void ValidatePaths(string targetPdfPath, string sourcePdfPath)
    {
        if (string.IsNullOrWhiteSpace(targetPdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(targetPdfPath));
        if (string.IsNullOrWhiteSpace(sourcePdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(sourcePdfPath));
    }
}
