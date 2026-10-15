using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Content;

/// <summary>
/// PDF affine transformation matrix <c>[a b c d e f]</c>, representing
/// <code>
/// | a c e |
/// | b d f |
/// | 0 0 1 |
/// </code>
/// </summary>
public readonly struct AffineTransform
{
    public double A { get; }
    public double B { get; }
    public double C { get; }
    public double D { get; }
    public double E { get; }
    public double F { get; }

    public AffineTransform(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public static AffineTransform Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>
    /// Returns the composition <paramref name="left"/> ∘ <paramref name="right"/>: the matrix that
    /// first applies <paramref name="right"/> to a point, then <paramref name="left"/>.
    /// </summary>
    public static AffineTransform Multiply(in AffineTransform left, in AffineTransform right) => new(
        a: left.A * right.A + left.C * right.B,
        b: left.B * right.A + left.D * right.B,
        c: left.A * right.C + left.C * right.D,
        d: left.B * right.C + left.D * right.D,
        e: left.A * right.E + left.C * right.F + left.E,
        f: left.B * right.E + left.D * right.F + left.F);

    /// <summary>
    /// PDF <c>cm</c> concatenation: the operand matrix is applied to a point first, then the
    /// current matrix. So a translation issued after a scale lands scaled in page space.
    /// </summary>
    public AffineTransform Concat(in AffineTransform operand) => Multiply(this, operand);

    public PointD Transform(PointD p) =>
        new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>Axis-aligned bounds of the unit square after transformation.</summary>
    public RectD TransformUnitSquare()
    {
        var p0 = Transform(new PointD(0, 0));
        var p1 = Transform(new PointD(1, 0));
        var p2 = Transform(new PointD(0, 1));
        var p3 = Transform(new PointD(1, 1));
        return RectD.FromPoints(p0, p1, p2, p3);
    }

    public override string ToString() => $"[{A:0.####} {B:0.####} {C:0.####} {D:0.####} {E:0.####} {F:0.####}]";
}
