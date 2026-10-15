using WatermarkRemover.Core.Content;
using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Watermarks;

/// <summary>
/// A set of page objects identified as watermark by one strategy on one page.
/// </summary>
public sealed class WatermarkMark
{
    public WatermarkMark(
        int pageIndex,
        string description,
        IReadOnlyList<ContentOperation> operations,
        IReadOnlyList<string> resourceNames,
        IReadOnlyList<int> annotationIndices,
        RectD? bounds = null)
    {
        PageIndex = pageIndex;
        Description = description;
        Operations = operations;
        ResourceNames = resourceNames;
        AnnotationIndices = annotationIndices;
        Bounds = bounds;
    }

    public int PageIndex { get; }

    public string Description { get; }

    /// <summary>Content-stream operations to delete.</summary>
    public IReadOnlyList<ContentOperation> Operations { get; }

    /// <summary>Resource names the mark draws through; they are pruned once unreferenced.</summary>
    public IReadOnlyList<string> ResourceNames { get; }

    /// <summary>Annotation indices to delete from the page.</summary>
    public IReadOnlyList<int> AnnotationIndices { get; }

    /// <summary>Optional page-space bounds of the mark, for reporting.</summary>
    public RectD? Bounds { get; }
}

/// <summary>
/// Detects one family of watermarks. Implement this to teach the tool about new watermark
/// features; detection is pure analysis and never mutates the document.
/// </summary>
public interface IWatermarkStrategy
{
    /// <summary>Stable identifier, e.g. <c>bottom-right-badge</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Returns every watermark instance found in the document (may be empty).</summary>
    IReadOnlyList<WatermarkMark> Detect(DocumentAnalysis document);
}
