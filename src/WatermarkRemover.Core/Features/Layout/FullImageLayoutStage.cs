using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// The always-available baseline block: hands the whole page through as one full-image region.
/// The pipeline short-circuits on <see cref="IsPassthrough"/> so a pipeline built on it is
/// byte-identical to recognising the page directly with the engine.
/// </summary>
public sealed class FullImageLayoutStage : ILayoutStage
{
    public static FullImageLayoutStage Instance { get; } = new();

    public string Name => "整图直通";

    public bool IsPassthrough => true;

    public Task<IReadOnlyList<LayoutRegion>> DetectRegionsAsync(byte[] imageBytes, IRegionProbe probe) =>
        Task.FromResult<IReadOnlyList<LayoutRegion>>(
            new[] { new LayoutRegion(new RectD(0, 0, 1, 1), "page") });
}
