using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// One recognised text line together with where it sits in the image (normalized 0..1, origin
/// top-left). <see cref="Bounds"/> is null when the underlying engine cannot report positions —
/// anchor-based layout stages then simply find no anchors and degrade to the full image.
/// </summary>
public sealed record RecognizedLine(string Text, RectD? Bounds);
