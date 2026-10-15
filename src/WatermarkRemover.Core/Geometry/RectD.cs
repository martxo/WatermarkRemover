namespace WatermarkRemover.Core.Geometry;

/// <summary>Axis-aligned rectangle in page user space (PDF points, origin bottom-left).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Bottom => Y;
    public double Right => X + Width;
    public double Top => Y + Height;

    public double Area => Math.Abs(Width * Height);

    /// <summary>Smallest axis-aligned rectangle containing all supplied corners.</summary>
    public static RectD FromPoints(params PointD[] points)
    {
        if (points.Length == 0) throw new ArgumentException("At least one point is required.", nameof(points));

        double minX = points[0].X, maxX = points[0].X;
        double minY = points[0].Y, maxY = points[0].Y;
        for (int i = 1; i < points.Length; i++)
        {
            minX = Math.Min(minX, points[i].X);
            maxX = Math.Max(maxX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxY = Math.Max(maxY, points[i].Y);
        }

        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    public bool Intersects(RectD other) =>
        Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;

    public override string ToString() => $"[{X:0.##}, {Y:0.##}, {Width:0.##}, {Height:0.##}]";
}

public readonly record struct PointD(double X, double Y);
