using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Watermarks;

/// <summary>Configurable knobs for <see cref="BottomRightBadgeStrategy"/>.</summary>
public sealed class BottomRightBadgeOptions
{
    /// <summary>
    /// The badge must fall inside the rightmost portion of the page, expressed as a fraction of
    /// page width. Default 0.35 (right 35%).
    /// </summary>
    public double RightEdgeFraction { get; init; } = 0.35;

    /// <summary>
    /// The badge must fall inside the bottommost portion of the page, expressed as a fraction of
    /// page height. Default 0.25 (bottom 25%).
    /// </summary>
    public double BottomEdgeFraction { get; init; } = 0.25;

    /// <summary>
    /// The badge must not cover more than this fraction of the page area, which keeps full-page
    /// scanned images from being mistaken for a watermark. Default 0.10.
    /// </summary>
    public double MaxAreaFraction { get; init; } = 0.10;

    /// <summary>
    /// Link annotations intersecting the badge by at least this fraction of the badge area are
    /// treated as part of the watermark. Default 0.5.
    /// </summary>
    public double AnnotationOverlapFraction { get; init; } = 0.5;
}

/// <summary>
/// Detects the fixed bottom-right corner badge used by CamScanner-style exports: a small image
/// stamped into the page corner, usually accompanied by a promotional link annotation.
/// </summary>
public sealed class BottomRightBadgeStrategy : IWatermarkStrategy
{
    private readonly BottomRightBadgeOptions _options;

    public BottomRightBadgeStrategy(BottomRightBadgeOptions? options = null)
    {
        _options = options ?? new BottomRightBadgeOptions();
    }

    public string Id => "bottom-right-badge";

    public string DisplayName => "右下角角标水印";

    public IReadOnlyList<WatermarkMark> Detect(DocumentAnalysis document)
    {
        var marks = new List<WatermarkMark>();

        foreach (var page in document.Pages)
        {
            var candidates = page.DrawnElements
                .Where(e => IsBadgeSized(e.Bounds, page.MediaBox) && IsInBottomRightCorner(e.Bounds, page.MediaBox))
                .ToList();

            if (candidates.Count == 0) continue;

            // Keep the whole corner cluster: badges are stamped as one visual unit.
            var operations = candidates.Select(e => e.Operation).ToList();
            var resourceNames = candidates.Select(e => e.ResourceName).Distinct().ToList();
            var bounds = candidates
                .Select(e => e.Bounds)
                .Aggregate((a, b) => RectD.FromPoints(
                    new PointD(a.Left, a.Bottom), new PointD(a.Right, a.Top),
                    new PointD(b.Left, b.Bottom), new PointD(b.Right, b.Top)));

            var annotations = page.Annotations
                .Where(a => Overlaps(a.Bounds, bounds))
                .Select(a => a.Index)
                .ToList();

            marks.Add(new WatermarkMark(
                page.Index,
                "右下角角标",
                operations,
                resourceNames,
                annotations,
                bounds));
        }

        return marks;
    }

    private bool IsBadgeSized(RectD bounds, RectD page)
    {
        double pageArea = page.Area;
        return pageArea > 0 && bounds.Area <= pageArea * _options.MaxAreaFraction;
    }

    private bool IsInBottomRightCorner(RectD bounds, RectD page)
    {
        double minX = page.Right - page.Width * _options.RightEdgeFraction;
        double maxY = page.Bottom + page.Height * _options.BottomEdgeFraction;
        return bounds.Right >= minX && bounds.Bottom <= maxY && bounds.Left >= minX;
    }

    private bool Overlaps(RectD a, RectD b)
    {
        if (!a.Intersects(b)) return false;
        double intersection =
            (Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
            (Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));
        double reference = Math.Min(a.Area, b.Area);
        return reference > 0 && intersection >= reference * _options.AnnotationOverlapFraction;
    }
}
