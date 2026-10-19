namespace WatermarkRemover.Core.Features.Preview;

/// <summary>
/// Page-index arithmetic for the PDF preview: clamping, boundary checks and the "第 N / M 页"
/// indicator. Kept UI-free so the paging rules stay unit testable.
/// </summary>
public static class PdfPreviewPaging
{
    /// <summary>Keeps a page index inside the document; an empty document collapses to page 0.</summary>
    public static int Clamp(int pageIndex, int pageCount) =>
        pageCount <= 0 ? 0 : Math.Clamp(pageIndex, 0, pageCount - 1);

    public static bool CanGoPrevious(int pageIndex) => pageIndex > 0;

    public static bool CanGoNext(int pageIndex, int pageCount) =>
        pageIndex >= 0 && pageIndex < pageCount - 1;

    /// <summary>Human-readable page indicator, e.g. "第 3 / 12 页".</summary>
    public static string FormatIndicator(int pageIndex, int pageCount) =>
        pageCount <= 0 ? "无页面" : $"第 {Clamp(pageIndex, pageCount) + 1} / {pageCount} 页";
}
