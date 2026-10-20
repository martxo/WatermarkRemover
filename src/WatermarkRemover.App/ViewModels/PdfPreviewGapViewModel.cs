namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One hover gap in a PDF strip: the gap after page i carries <see cref="InsertAfterIndex"/>
/// = i, so scanning into it splices the fresh pages between page i and the one that follows;
/// the bookend gaps carry -1 (before the first page) and the last page's index (append after
/// it). Pure marker — its template's button binds the strip's shared
/// <c>InsertScanAfterCommand</c> with this index, and <see cref="ToolTip"/> names the slot.
/// </summary>
public sealed record PdfPreviewGapViewModel(int InsertAfterIndex, string ToolTip);
