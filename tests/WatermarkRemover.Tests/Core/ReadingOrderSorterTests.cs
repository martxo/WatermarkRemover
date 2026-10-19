using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Tests.Core;

public class ReadingOrderSorterTests
{
    private static LayoutRegion R(double x, double y, double w, double h, string kind) =>
        new(new RectD(x, y, w, h), kind);

    [Fact]
    public void SortsRowsTopToBottomEvenWhenRegionsArriveOutOfOrder()
    {
        LayoutRegion bottom = R(0.1, 0.6, 0.4, 0.2, "text");
        LayoutRegion top = R(0.2, 0.1, 0.3, 0.2, "text");

        var sorted = ReadingOrderSorter.Sort(new[] { bottom, top });

        Assert.Equal(new[] { top, bottom }, sorted);
    }

    [Fact]
    public void SortsInsideARowLeftToRight()
    {
        LayoutRegion left = R(0.1, 0.1, 0.3, 0.1, "text");
        LayoutRegion right = R(0.55, 0.12, 0.3, 0.1, "text");
        LayoutRegion nextRow = R(0.1, 0.6, 0.3, 0.1, "text");

        var sorted = ReadingOrderSorter.Sort(new[] { nextRow, right, left });

        Assert.Equal(new[] { left, right, nextRow }, sorted);
    }

    [Fact]
    public void SameColumnFallsBackToKindTitleBeforeFigure()
    {
        // Figure sits slightly left of the title, but within the same column — kind wins.
        LayoutRegion figure = R(0.10, 0.10, 0.2, 0.1, "image");
        LayoutRegion title = R(0.11, 0.11, 0.4, 0.1, "paragraph_title");

        var sorted = ReadingOrderSorter.Sort(new[] { figure, title });

        Assert.Equal(new[] { title, figure }, sorted);
    }

    [Fact]
    public void KindOrderIsTitleBodyTableFigureWithinOneColumn()
    {
        LayoutRegion figure = R(0.10, 0.10, 0.2, 0.1, "figure");
        LayoutRegion table = R(0.10, 0.11, 0.2, 0.1, "table");
        LayoutRegion body = R(0.10, 0.12, 0.2, 0.1, "text");
        LayoutRegion title = R(0.10, 0.13, 0.2, 0.1, "title");

        var sorted = ReadingOrderSorter.Sort(new[] { figure, table, body, title });

        Assert.Equal(new[] { title, body, table, figure }, sorted);
    }

    [Fact]
    public void SingleRegionIsReturnedUntouched()
    {
        LayoutRegion only = R(0, 0, 1, 1, "page");

        var sorted = ReadingOrderSorter.Sort(new[] { only });

        Assert.Same(only, sorted[0]);
    }
}
