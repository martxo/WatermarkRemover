namespace WatermarkRemover.Imaging.Screenshot;

/// <summary>Which mark the user drew over the captured region.</summary>
public enum ScreenshotAnnotationKind
{
    Rectangle,
    Arrow,
    Pen,
    Text,
    Mosaic,
}

/// <summary>A point in crop-local physical pixels (0,0 = the selection's top-left).</summary>
public readonly record struct ScreenshotPointF(float X, float Y);

/// <summary>
/// One annotation of a screenshot. Coordinates are crop-local physical pixels, so the composer
/// can render at capture resolution without knowing anything about monitors or DPI.
/// </summary>
public sealed class ScreenshotAnnotation
{
    /// <summary>Default mark colour: the red every screenshot tool starts with.</summary>
    public const uint DefaultColorArgb = 0xFFE53935;

    public ScreenshotAnnotationKind Kind { get; init; }

    public IReadOnlyList<ScreenshotPointF> Points { get; init; } = Array.Empty<ScreenshotPointF>();

    public uint ColorArgb { get; init; } = DefaultColorArgb;

    /// <summary>Stroke width in physical pixels.</summary>
    public float Thickness { get; init; } = 3f;

    public string? Text { get; init; }

    /// <summary>Text height in physical pixels.</summary>
    public float FontSize { get; init; } = 20f;

    public static ScreenshotAnnotation Rectangle(float x1, float y1, float x2, float y2, float thickness) => new()
    {
        Kind = ScreenshotAnnotationKind.Rectangle,
        Points = new[] { new ScreenshotPointF(x1, y1), new ScreenshotPointF(x2, y2) },
        Thickness = thickness,
    };

    public static ScreenshotAnnotation Arrow(float x1, float y1, float x2, float y2, float thickness) => new()
    {
        Kind = ScreenshotAnnotationKind.Arrow,
        Points = new[] { new ScreenshotPointF(x1, y1), new ScreenshotPointF(x2, y2) },
        Thickness = thickness,
    };

    public static ScreenshotAnnotation Pen(IReadOnlyList<ScreenshotPointF> points, float thickness) => new()
    {
        Kind = ScreenshotAnnotationKind.Pen,
        Points = points,
        Thickness = thickness,
    };

    public static ScreenshotAnnotation TextAt(float x, float y, string text, float fontSize) => new()
    {
        Kind = ScreenshotAnnotationKind.Text,
        Points = new[] { new ScreenshotPointF(x, y) },
        Text = text,
        FontSize = fontSize,
        Thickness = 1f,
    };

    public static ScreenshotAnnotation Mosaic(float x1, float y1, float x2, float y2, float thickness) => new()
    {
        Kind = ScreenshotAnnotationKind.Mosaic,
        Points = new[] { new ScreenshotPointF(x1, y1), new ScreenshotPointF(x2, y2) },
        Thickness = thickness,
    };
}
