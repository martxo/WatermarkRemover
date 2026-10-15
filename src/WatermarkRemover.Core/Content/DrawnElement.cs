using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Content;

/// <summary>
/// A drawing operation captured from a page content stream: which XObject was painted and
/// where its unit square landed in page user space.
/// </summary>
public sealed class DrawnElement
{
    public DrawnElement(int pageIndex, string resourceName, RectD bounds, ContentOperation operation)
    {
        PageIndex = pageIndex;
        ResourceName = resourceName;
        Bounds = bounds;
        Operation = operation;
    }

    public int PageIndex { get; }

    /// <summary>XObject resource name without the leading slash, e.g. <c>Im2</c>.</summary>
    public string ResourceName { get; }

    /// <summary>Axis-aligned bounds of the painted XObject in page user space.</summary>
    public RectD Bounds { get; }

    /// <summary>The <c>Do</c> operation that painted it.</summary>
    public ContentOperation Operation { get; }

    public override string ToString() =>
        $"#{PageIndex} {ResourceName} at {Bounds}";
}
