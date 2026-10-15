using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Documents;

/// <summary>A page as extracted from a document source, prior to any analysis.</summary>
public sealed class RawPage
{
    public RawPage(
        int index,
        RectD mediaBox,
        byte[] contentStream,
        IReadOnlyList<string> xObjectNames,
        IReadOnlyList<RawAnnotation> annotations)
    {
        Index = index;
        MediaBox = mediaBox;
        ContentStream = contentStream;
        XObjectNames = xObjectNames;
        Annotations = annotations;
    }

    public int Index { get; }

    public RectD MediaBox { get; }

    /// <summary>Decoded content stream bytes.</summary>
    public byte[] ContentStream { get; }

    /// <summary>XObject resource names, without leading slash.</summary>
    public IReadOnlyList<string> XObjectNames { get; }

    public IReadOnlyList<RawAnnotation> Annotations { get; }
}

public sealed record RawAnnotation(int Index, RectD Bounds, string? Uri);

/// <summary>A document as extracted from a source, before parsing.</summary>
public sealed class RawDocument
{
    public RawDocument(IReadOnlyList<RawPage> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<RawPage> Pages { get; }
}

/// <summary>
/// The concrete edits to apply to one page. Produced by the strategies through the processor and
/// consumed by a document sink. This is the seam between pure analysis and document I/O.
/// </summary>
public sealed class PageEdit
{
    public PageEdit(
        int pageIndex,
        byte[] newContentStream,
        IReadOnlyList<string> removedResourceNames,
        IReadOnlyList<int> removedAnnotationIndices)
    {
        PageIndex = pageIndex;
        NewContentStream = newContentStream;
        RemovedResourceNames = removedResourceNames;
        RemovedAnnotationIndices = removedAnnotationIndices;
    }

    public int PageIndex { get; }

    public byte[] NewContentStream { get; }

    /// <summary>XObject resources to drop from the page (orphans are pruned by the sink).</summary>
    public IReadOnlyList<string> RemovedResourceNames { get; }

    public IReadOnlyList<int> RemovedAnnotationIndices { get; }
}
