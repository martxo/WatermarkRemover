namespace WatermarkRemover.Core.Features.Screenshot;

/// <summary>
/// A rectangle in screen (physical) pixels. The overlay works across monitors whose origins can
/// be negative, so it cannot use a window-relative Rect; this small value type keeps the
/// normalisation/clamping rules in one testable place.
/// </summary>
public readonly record struct ScreenshotRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public double CenterX => Left + Width / 2;

    public double CenterY => Top + Height / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>A rect built from two drag points, regardless of drag direction.</summary>
    public static ScreenshotRect FromPoints(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>Moves negative widths/heights onto positive axes (dragging up-left).</summary>
    public ScreenshotRect Normalized() =>
        Width >= 0 && Height >= 0 ? this : FromPoints(Left, Top, Right, Bottom);

    public ScreenshotRect Offset(double dx, double dy) => new(Left + dx, Top + dy, Width, Height);

    /// <summary>Overlap with another rect; an empty rect when they do not intersect.</summary>
    public ScreenshotRect Intersect(ScreenshotRect other)
    {
        double left = Math.Max(Left, other.Left);
        double top = Math.Max(Top, other.Top);
        double right = Math.Min(Right, other.Right);
        double bottom = Math.Min(Bottom, other.Bottom);
        return new ScreenshotRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Keeps the rect inside the desktop, shrinking it when it sticks out.</summary>
    public ScreenshotRect ClampTo(ScreenshotRect bounds)
    {
        double left = Math.Max(Left, bounds.Left);
        double top = Math.Max(Top, bounds.Top);
        double right = Math.Min(Right, bounds.Right);
        double bottom = Math.Min(Bottom, bounds.Bottom);
        return new ScreenshotRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

/// <summary>The part of the selection a press landed on.</summary>
public enum ScreenshotHandle
{
    None,
    Inside,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>Selection hit-testing shared by every monitor overlay.</summary>
public static class ScreenshotGeometry
{
    /// <summary>Distance (physical px) around an edge/corner that still grabs the handle.</summary>
    public const double HandleTolerance = 8;

    /// <summary>Smallest usable selection; below it a drag reads as a click and is discarded.</summary>
    public const double MinimumSelectionSide = 6;

    /// <summary>
    /// Which handle (if any) a point grabs. Corners win over edges, edges over the interior,
    /// so a press near a corner always resizes instead of moving.
    /// </summary>
    public static ScreenshotHandle HitTest(ScreenshotRect selection, double x, double y, double tolerance = HandleTolerance)
    {
        if (selection.IsEmpty) return ScreenshotHandle.None;

        double t = Math.Max(1, tolerance);
        bool nearLeft = Math.Abs(x - selection.Left) <= t;
        bool nearRight = Math.Abs(x - selection.Right) <= t;
        bool nearTop = Math.Abs(y - selection.Top) <= t;
        bool nearBottom = Math.Abs(y - selection.Bottom) <= t;
        bool withinX = x >= selection.Left - t && x <= selection.Right + t;
        bool withinY = y >= selection.Top - t && y <= selection.Bottom + t;

        if (withinX && withinY)
        {
            if (nearLeft && nearTop) return ScreenshotHandle.TopLeft;
            if (nearRight && nearTop) return ScreenshotHandle.TopRight;
            if (nearLeft && nearBottom) return ScreenshotHandle.BottomLeft;
            if (nearRight && nearBottom) return ScreenshotHandle.BottomRight;
            if (nearLeft) return ScreenshotHandle.Left;
            if (nearRight) return ScreenshotHandle.Right;
            if (nearTop) return ScreenshotHandle.Top;
            if (nearBottom) return ScreenshotHandle.Bottom;
        }

        return selection.Contains(x, y) ? ScreenshotHandle.Inside : ScreenshotHandle.None;
    }

    /// <summary>Applies a handle drag to the selection and keeps it inside <paramref name="bounds"/>.</summary>
    public static ScreenshotRect Resize(
        ScreenshotRect original, ScreenshotHandle handle, double x, double y, ScreenshotRect bounds)
    {
        double left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;

        if (handle is ScreenshotHandle.Left or ScreenshotHandle.TopLeft or ScreenshotHandle.BottomLeft)
            left = x;
        if (handle is ScreenshotHandle.Right or ScreenshotHandle.TopRight or ScreenshotHandle.BottomRight)
            right = x;
        if (handle is ScreenshotHandle.Top or ScreenshotHandle.TopLeft or ScreenshotHandle.TopRight)
            top = y;
        if (handle is ScreenshotHandle.Bottom or ScreenshotHandle.BottomLeft or ScreenshotHandle.BottomRight)
            bottom = y;

        var resized = ScreenshotRect.FromPoints(left, top, right, bottom).ClampTo(bounds);
        if (resized.Width < MinimumSelectionSide || resized.Height < MinimumSelectionSide)
            return original;
        return resized;
    }
}
