namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// Resolves the best usable layout block by probing registered factories in priority order —
/// the counterpart of <c>OcrEngineChain</c> for the pipeline's layout stage. The first registered
/// block is the always-available full-image passthrough, and when nothing else is usable the
/// chain degrades to it, so a machine missing every model still recognises exactly like the
/// direct path instead of failing.
/// </summary>
public sealed class LayoutStageChain
{
    private readonly ProbeChain<ILayoutStage> _chain;

    public LayoutStageChain(IEnumerable<IProbeFactory<ILayoutStage>> factories)
    {
        _chain = new ProbeChain<ILayoutStage>(factories, _ => new FullImageLayoutStage());
    }

    /// <summary>The first usable block, or the full-image passthrough when nothing else works.</summary>
    public ILayoutStage Stage => _chain.Product;

    /// <summary>Registered factories in priority order; name and description read without probing.</summary>
    public IReadOnlyList<IProbeFactory<ILayoutStage>> Factories => _chain.Factories;

    /// <summary>Probe results so far, in chain order.</summary>
    public IReadOnlyList<ProbeLink<ILayoutStage>> Links => _chain.Links;

    /// <summary>Whether a real (non-fallback) block was resolved.</summary>
    public bool HasStage => _chain.HasAvailable;

    /// <summary>Probes every remaining factory once so the settings page can show availability.</summary>
    public void EnsureAllProbed() => _chain.EnsureAllProbed();

    /// <summary>Resolves the user's preferred block, falling back to the automatic chain.</summary>
    public ILayoutStage Select(string? preferredName) => _chain.Select(preferredName);

    /// <summary>One line per probed link: "名称：可用" / "名称：不可用（原因）".</summary>
    public string Describe() => _chain.Describe();
}
