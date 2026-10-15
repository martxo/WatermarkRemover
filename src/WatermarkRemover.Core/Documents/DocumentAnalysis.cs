using WatermarkRemover.Core.Content;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Documents;

/// <summary>A clickable link annotation on a page, as seen during analysis.</summary>
public sealed record LinkAnnotation(int Index, RectD Bounds, string? Uri);

/// <summary>Everything a watermark strategy needs to inspect one page.</summary>
public sealed class PageAnalysis
{
    public PageAnalysis(
        int index,
        RectD mediaBox,
        ParsedContentStream content,
        IReadOnlyList<DrawnElement> drawnElements,
        IReadOnlyList<string> xObjectNames,
        IReadOnlyList<LinkAnnotation> annotations)
    {
        Index = index;
        MediaBox = mediaBox;
        Content = content;
        DrawnElements = drawnElements;
        XObjectNames = xObjectNames;
        Annotations = annotations;
    }

    /// <summary>Zero-based page index.</summary>
    public int Index { get; }

    public RectD MediaBox { get; }

    public ParsedContentStream Content { get; }

    /// <summary>Every XObject painted on this page, with its page-space bounds.</summary>
    public IReadOnlyList<DrawnElement> DrawnElements { get; }

    /// <summary>XObject resource names declared for this page (without leading slash).</summary>
    public IReadOnlyList<string> XObjectNames { get; }

    public IReadOnlyList<LinkAnnotation> Annotations { get; }
}

/// <summary>A parsed document, ready for watermark detection.</summary>
public sealed class DocumentAnalysis
{
    public DocumentAnalysis(IReadOnlyList<PageAnalysis> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<PageAnalysis> Pages { get; }
}
