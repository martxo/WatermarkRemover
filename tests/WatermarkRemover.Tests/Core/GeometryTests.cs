using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Tests.Core;

public class GeometryTests
{
    [Fact]
    public void FromPoints_computes_bounding_box()
    {
        var r = RectD.FromPoints(
            new PointD(2, 3),
            new PointD(10, 8),
            new PointD(-1, 5));

        Assert.Equal(-1, r.X);
        Assert.Equal(3, r.Y);
        Assert.Equal(11, r.Width);
        Assert.Equal(5, r.Height);
        Assert.Equal(55, r.Area);
    }

    [Fact]
    public void FromPoints_requires_at_least_one_point()
    {
        Assert.Throws<ArgumentException>(() => RectD.FromPoints());
    }

    [Fact]
    public void Intersects_detects_overlap()
    {
        var a = new RectD(0, 0, 10, 10);
        var b = new RectD(5, 5, 10, 10);

        Assert.True(a.Intersects(b));
        Assert.True(b.Intersects(a));
    }

    [Fact]
    public void Intersects_is_false_for_disjoint_rectangles()
    {
        var a = new RectD(0, 0, 10, 10);
        var b = new RectD(20, 20, 10, 10);

        Assert.False(a.Intersects(b));
    }

    [Fact]
    public void Intersects_is_false_for_merely_touching_edges()
    {
        var a = new RectD(0, 0, 10, 10);
        var b = new RectD(10, 0, 10, 10);

        Assert.False(a.Intersects(b));
    }
}
