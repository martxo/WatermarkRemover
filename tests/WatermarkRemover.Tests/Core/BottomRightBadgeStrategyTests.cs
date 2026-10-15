using System.Text;
using WatermarkRemover.Core.Content;
using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Watermarks;

namespace WatermarkRemover.Tests.Core;

public class BottomRightBadgeStrategyTests
{
    private const int PageWidth = 842;
    private const int PageHeight = 595;

    private static DocumentAnalysis BuildDocument(params (string Name, double W, double H, double X, double Y)[] draws)
    {
        var sb = new StringBuilder();
        foreach (var (name, w, h, x, y) in draws)
            sb.Append(FormattableString.Invariant($"q {w} 0 0 {h} {x} {y} cm /{name} Do Q\n"));

        return BuildDocument(sb.ToString(), Array.Empty<(int, int, int, int, string?)>());
    }

    private static DocumentAnalysis BuildDocument(
        string content,
        IReadOnlyList<(int X1, int Y1, int X2, int Y2, string? Uri)> annotations)
    {
        var parsed = ParsedContentStream.Parse(Encoding.Latin1.GetBytes(content));
        var drawn = new ContentInterpreter().Interpret(parsed);
        var page = new PageAnalysis(
            0,
            new RectD(0, 0, PageWidth, PageHeight),
            parsed,
            drawn,
            drawn.Select(d => d.ResourceName).Distinct().ToList(),
            annotations.Select((a, i) => new LinkAnnotation(i, new RectD(a.X1, a.Y1, a.X2 - a.X1, a.Y2 - a.Y1), a.Uri)).ToList());

        return new DocumentAnalysis(new[] { page });
    }

    [Fact]
    public void Detects_the_corner_badge_from_the_sample_document()
    {
        // Exactly the geometry of the CamScanner export.
        var doc = BuildDocument(
            ("Im1", 720.4683, 543, 60.76585, 52),
            ("Im2", 104, 30, 723, 10));

        var marks = new BottomRightBadgeStrategy().Detect(doc);

        var mark = Assert.Single(marks);
        Assert.Equal("Im2", Assert.Single(mark.ResourceNames));
        Assert.Single(mark.Operations);
    }

    [Fact]
    public void Ignores_the_full_page_scan()
    {
        var doc = BuildDocument(("Im1", 720, 543, 60, 52));

        Assert.Empty(new BottomRightBadgeStrategy().Detect(doc));
    }

    [Fact]
    public void Ignores_elements_outside_the_corner()
    {
        var doc = BuildDocument(("Logo", 104, 30, 100, 500));

        Assert.Empty(new BottomRightBadgeStrategy().Detect(doc));
    }

    [Fact]
    public void Ignores_a_large_overlay_covering_the_whole_page()
    {
        // Big translucent overlay that happens to extend into the corner: not a badge.
        var doc = BuildDocument(("Overlay", 800, 550, 20, 20));

        Assert.Empty(new BottomRightBadgeStrategy().Detect(doc));
    }

    [Fact]
    public void Captures_the_link_annotation_sitting_on_the_badge()
    {
        var doc = BuildDocument(
            "q 104 0 0 30 723 10 cm /Im2 Do Q\n",
            new[] { (723, 10, 827, 40, (string?)"https://example.com/download") });

        var mark = Assert.Single(new BottomRightBadgeStrategy().Detect(doc));
        Assert.Equal(0, Assert.Single(mark.AnnotationIndices));
    }

    [Fact]
    public void Ignores_annotations_far_from_the_badge()
    {
        var doc = BuildDocument(
            "q 104 0 0 30 723 10 cm /Im2 Do Q\n",
            new[] { (50, 500, 200, 540, (string?)"https://example.com/elsewhere") });

        var mark = Assert.Single(new BottomRightBadgeStrategy().Detect(doc));
        Assert.Empty(mark.AnnotationIndices);
    }

    [Fact]
    public void Finds_a_badge_on_every_page()
    {
        var parsed = ParsedContentStream.Parse(Encoding.Latin1.GetBytes("q 104 0 0 30 723 10 cm /Im2 Do Q\n"));
        var drawn = new ContentInterpreter().Interpret(parsed);
        PageAnalysis Make(int index) => new(
            index,
            new RectD(0, 0, PageWidth, PageHeight),
            parsed,
            drawn,
            new[] { "Im2" },
            Array.Empty<LinkAnnotation>());

        var doc = new DocumentAnalysis(new[] { Make(0), Make(1), Make(2) });

        var marks = new BottomRightBadgeStrategy().Detect(doc);
        Assert.Equal(3, marks.Count);
        Assert.Equal(new[] { 0, 1, 2 }, marks.Select(m => m.PageIndex).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void Honours_custom_corner_thresholds()
    {
        var strict = new BottomRightBadgeStrategy(new BottomRightBadgeOptions
        {
            RightEdgeFraction = 0.05,
            BottomEdgeFraction = 0.05,
        });

        // Badge occupies the right 12% / bottom 5%: outside a 5% x 5% corner.
        var doc = BuildDocument(("Im2", 100, 29, 742, 10));

        Assert.Empty(strict.Detect(doc));
    }
}
