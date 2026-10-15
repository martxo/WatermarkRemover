using WatermarkRemover.Core.Content;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Tests.Core;

public class AffineTransformTests
{
    [Fact]
    public void Identity_leaves_points_unchanged()
    {
        var p = AffineTransform.Identity.Transform(new PointD(3, 7));
        Assert.Equal(3, p.X);
        Assert.Equal(7, p.Y);
    }

    [Fact]
    public void Translation_only_moves_the_origin()
    {
        var m = new AffineTransform(1, 0, 0, 1, 10, 20);
        var p = m.Transform(new PointD(0, 0));

        Assert.Equal(10, p.X);
        Assert.Equal(20, p.Y);
    }

    [Fact]
    public void Unit_square_bounds_follow_scale_and_translation()
    {
        // Matches the sample badge: "104 0 0 30 723 10 cm"
        var m = new AffineTransform(104, 0, 0, 30, 723, 10);
        var bounds = m.TransformUnitSquare();

        Assert.Equal(723, bounds.X);
        Assert.Equal(10, bounds.Y);
        Assert.Equal(104, bounds.Width);
        Assert.Equal(30, bounds.Height);
    }

    [Fact]
    public void Multiply_composes_left_after_right()
    {
        // (L ∘ R) applied to a point equals L applied to (R applied to the point).
        var r = new AffineTransform(1, 0, 0, 1, 100, 0); // translate x+100
        var l = new AffineTransform(2, 0, 0, 2, 0, 0);   // scale 2

        var composed = AffineTransform.Multiply(l, r);
        var direct = l.Transform(r.Transform(new PointD(1, 1)));

        var via = composed.Transform(new PointD(1, 1));
        Assert.Equal(direct.X, via.X);
        Assert.Equal(direct.Y, via.Y);
    }

    [Fact]
    public void Concat_applies_the_operand_before_the_current_matrix()
    {
        // A translation issued after a scale lands scaled in page space.
        var scale = new AffineTransform(2, 0, 0, 2, 0, 0);
        var translate = new AffineTransform(1, 0, 0, 1, 100, 0);

        var ctM = scale.Concat(translate);

        Assert.Equal(200, ctM.Transform(new PointD(0, 0)).X);
        Assert.Equal(2, ctM.Transform(new PointD(1, 0)).X - ctM.Transform(new PointD(0, 0)).X);
    }

    [Fact]
    public void Rotation_bounds_are_axis_aligned()
    {
        // 90° rotation: (x, y) -> (-y, x).
        var m = new AffineTransform(0, 1, -1, 0, 0, 0);
        var bounds = m.TransformUnitSquare();

        Assert.Equal(-1, bounds.X);
        Assert.Equal(0, bounds.Y);
        Assert.Equal(1, bounds.Width);
        Assert.Equal(1, bounds.Height);
    }

    [Fact]
    public void Composition_is_not_commutative()
    {
        // scale then translate puts the origin at 200 (the translate gets scaled);
        // translate then scale puts it at 100 (the translate does not).
        var t = new AffineTransform(1, 0, 0, 1, 100, 0);
        var s = new AffineTransform(2, 0, 0, 2, 0, 0);

        var scaleThenTranslate = s.Concat(t);
        var translateThenScale = t.Concat(s);

        Assert.Equal(200, scaleThenTranslate.Transform(new PointD(0, 0)).X);
        Assert.Equal(100, translateThenScale.Transform(new PointD(0, 0)).X);
    }
}
