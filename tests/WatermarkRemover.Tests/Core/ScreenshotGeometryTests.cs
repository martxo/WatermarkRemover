using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.Tests.Core;

public sealed class ScreenshotGeometryTests
{
    [Fact]
    public void Dragging_up_left_still_produces_a_positive_rect()
    {
        var rect = ScreenshotRect.FromPoints(200, 180, 40, 20);

        Assert.Equal(40, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(160, rect.Width);
        Assert.Equal(160, rect.Height);
    }

    [Fact]
    public void Intersect_returns_empty_for_disjoint_rects()
    {
        var a = new ScreenshotRect(0, 0, 100, 100);
        var b = new ScreenshotRect(150, 0, 50, 50);

        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Clamp_keeps_the_selection_on_the_desktop()
    {
        var bounds = new ScreenshotRect(-1920, 0, 3840, 1080);
        var selection = new ScreenshotRect(-2000, -50, 300, 200);

        var clamped = selection.ClampTo(bounds);

        Assert.Equal(-1920, clamped.Left);
        Assert.Equal(0, clamped.Top);
        Assert.Equal(220, clamped.Width);
        Assert.Equal(150, clamped.Height);
    }

    [Fact]
    public void Corners_win_over_edges_and_edges_win_over_inside()
    {
        var selection = new ScreenshotRect(100, 100, 200, 100);

        Assert.Equal(ScreenshotHandle.TopLeft, ScreenshotGeometry.HitTest(selection, 101, 101));
        Assert.Equal(ScreenshotHandle.BottomRight, ScreenshotGeometry.HitTest(selection, 299, 199));
        Assert.Equal(ScreenshotHandle.Left, ScreenshotGeometry.HitTest(selection, 101, 150));
        Assert.Equal(ScreenshotHandle.Top, ScreenshotGeometry.HitTest(selection, 200, 101));
        Assert.Equal(ScreenshotHandle.Inside, ScreenshotGeometry.HitTest(selection, 200, 150));
        Assert.Equal(ScreenshotHandle.None, ScreenshotGeometry.HitTest(selection, 500, 500));
    }

    [Fact]
    public void Resize_moves_only_the_dragged_edge_and_respects_bounds()
    {
        var original = new ScreenshotRect(100, 100, 200, 100);
        var bounds = new ScreenshotRect(0, 0, 1000, 1000);

        var resized = ScreenshotGeometry.Resize(original, ScreenshotHandle.Right, 350, 200, bounds);

        Assert.Equal(100, resized.Left);
        Assert.Equal(100, resized.Top);
        Assert.Equal(250, resized.Width);
        Assert.Equal(100, resized.Height);
    }

    [Fact]
    public void A_too_small_resize_keeps_the_previous_selection()
    {
        var original = new ScreenshotRect(100, 100, 200, 100);
        var bounds = new ScreenshotRect(0, 0, 1000, 1000);

        var resized = ScreenshotGeometry.Resize(original, ScreenshotHandle.Right, 102, 150, bounds);

        Assert.Equal(original, resized); // 2px wide would be unusable
    }
}
