using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Features.Layout;

/// <summary>One detected region of a page. Bounds is normalized (0..1) against the image
/// handed to the layout stage, origin top-left (image space, y grows downward).
/// Kind is a free-form category (title / text / table / figure …) used only for reading-order ties.</summary>
public sealed record LayoutRegion(RectD Bounds, string Kind, float Score = 1f);
