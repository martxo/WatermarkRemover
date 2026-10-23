namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Resolves the best usable OCR engine by probing registered factories in priority order,
/// degrading safely: a machine missing one engine simply falls through to the next. When no
/// engine is usable the chain yields an <see cref="UnavailableOcrEngine"/> that fails per job
/// (not at startup) with the full probe history.
/// </summary>
public sealed class OcrEngineChain
{
    private readonly ProbeChain<IOcrEngine> _chain;

    public OcrEngineChain(IEnumerable<IProbeFactory<IOcrEngine>> factories)
    {
        _chain = new ProbeChain<IOcrEngine>(factories, DescribeFailures);
    }

    /// <summary>The first usable engine, or a failing stub when the whole chain is unavailable.</summary>
    public IOcrEngine Engine => _chain.Product;

    /// <summary>Registered factories in priority order; name and description read without probing.</summary>
    public IReadOnlyList<IProbeFactory<IOcrEngine>> Factories => _chain.Factories;

    /// <summary>Probe results so far, in chain order.</summary>
    public IReadOnlyList<ProbeLink<IOcrEngine>> Links => _chain.Links;

    /// <summary>Whether a real engine was resolved (the stub does not count).</summary>
    public bool HasEngine => _chain.HasAvailable;

    /// <summary>Probes every remaining factory once so the settings page can show availability.</summary>
    public void EnsureAllProbed() => _chain.EnsureAllProbed();

    /// <summary>Resolves the user's preferred engine, falling back to the automatic chain.</summary>
    public IOcrEngine Select(string? preferredName) => _chain.Select(preferredName);

    /// <summary>One line per probed link: "名称：可用" / "名称：不可用（原因）".</summary>
    public string Describe() => _chain.Describe();

    private static UnavailableOcrEngine DescribeFailures(IReadOnlyList<ProbeLink<IOcrEngine>> links) =>
        new(links.Count == 0
            ? "未注册任何 OCR 引擎"
            : string.Join("；", links.Select(l => $"{l.Name} 不可用（{l.Reason}）")));
}
