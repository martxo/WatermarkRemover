namespace WatermarkRemover.Core.Features.Layout;

/// <summary>Outcome of probing one chain link.</summary>
public sealed record LayoutStageLink(string Name, ILayoutStage? Stage, bool Available, string Reason);

/// <summary>
/// Resolves the best usable layout block by probing registered factories in priority order —
/// the counterpart of <c>OcrEngineChain</c> for the pipeline's layout stage. The first
/// registered block is the always-available full-image passthrough, and when nothing else is
/// usable the chain degrades to it, so a machine missing every model still recognises exactly
/// like the direct path instead of failing. Probing stops at the first usable block at startup;
/// <see cref="EnsureAllProbed"/> continues on demand for the settings page, and <see cref="Select"/>
/// resolves a user-chosen preference with safe fallback.
/// </summary>
public sealed class LayoutStageChain
{
    private readonly object _gate = new();
    private readonly ILayoutStageFactory[] _factories;
    private readonly List<LayoutStageLink> _links = new();
    private ILayoutStage? _resolved;
    private int _next;

    public LayoutStageChain(IEnumerable<ILayoutStageFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories.ToArray();

        lock (_gate)
        {
            while (_next < _factories.Length && _resolved is null) ProbeOne();
            _resolved ??= new FullImageLayoutStage();
        }

        Stage = _resolved;
    }

    /// <summary>The first usable block, or the full-image passthrough when nothing else works.</summary>
    public ILayoutStage Stage { get; }

    /// <summary>Registered factories in priority order; name and description read without probing.</summary>
    public IReadOnlyList<ILayoutStageFactory> Factories => _factories;

    /// <summary>Probe results so far, in chain order; stops at the first available block until
    /// <see cref="EnsureAllProbed"/> / <see cref="Select"/> continues probing.</summary>
    public IReadOnlyList<LayoutStageLink> Links
    {
        get { lock (_gate) return _links.ToArray(); }
    }

    /// <summary>Whether a real (non-fallback) block was resolved.</summary>
    public bool HasStage => Links.Any(l => l.Available);

    /// <summary>
    /// Probes every remaining factory once, regardless of the block already resolved at
    /// startup — this is what lets the settings page show each block's availability.
    /// </summary>
    public void EnsureAllProbed()
    {
        lock (_gate)
        {
            while (_next < _factories.Length) ProbeOne();
        }
    }

    /// <summary>
    /// Resolves the user's preferred block (by factory name), probing further factories only
    /// when the preference is not answered yet. An empty preference, an unknown name or an
    /// unusable block falls back to the automatic priority chain (<see cref="Stage"/>), so a
    /// stale preference can never leave the app without a layout stage.
    /// </summary>
    public ILayoutStage Select(string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) return Stage;

        lock (_gate)
        {
            LayoutStageLink? match = FindAvailable(preferredName);
            while (match is null && _next < _factories.Length)
            {
                ProbeOne();
                LayoutStageLink last = _links[^1];
                if (last.Available && string.Equals(last.Name, preferredName, StringComparison.Ordinal))
                    match = last;
            }

            if (match?.Stage is not null) return match.Stage;
        }

        return Stage;
    }

    /// <summary>One line per probed link: "名称：可用" / "名称：不可用（原因）".</summary>
    public string Describe() =>
        string.Join(Environment.NewLine, Links.Select(DescribeLink));

    private LayoutStageLink? FindAvailable(string preferredName) =>
        _links.FirstOrDefault(l =>
            l.Available && string.Equals(l.Name, preferredName, StringComparison.Ordinal));

    /// <summary>Probes exactly one factory and records the outcome. Caller holds <see cref="_gate"/>.</summary>
    private void ProbeOne()
    {
        ILayoutStageFactory factory = _factories[_next++];
        try
        {
            if (factory.TryCreate(out ILayoutStage? stage, out string reason) && stage is not null)
            {
                _links.Add(new LayoutStageLink(factory.Name, stage, true, string.Empty));
                _resolved ??= stage;
            }
            else
            {
                _links.Add(new LayoutStageLink(factory.Name, null, false, reason));
            }
        }
        catch (Exception ex)
        {
            // A broken block must not break the chain — record it and keep degrading.
            _links.Add(new LayoutStageLink(factory.Name, null, false, ex.Message));
        }
    }

    private static string DescribeLink(LayoutStageLink link) =>
        link.Available ? $"{link.Name}：可用" : $"{link.Name}：不可用（{link.Reason}）";
}
