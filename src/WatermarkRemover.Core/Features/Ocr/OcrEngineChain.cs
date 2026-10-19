namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>Outcome of probing one chain link.</summary>
public sealed record OcrEngineLink(string Name, IOcrEngine? Engine, bool Available, string Reason);

/// <summary>
/// Resolves the best usable OCR engine by probing registered factories in priority order,
/// degrading safely: a machine missing one engine simply falls through to the next. The same
/// probe result feeds runtime selection, the settings page and the diagnostics report, so the
/// three can never drift apart. Probing stops at the first usable engine at startup (cheap);
/// <see cref="EnsureAllProbed"/> continues on demand when the settings page needs every engine's
/// availability, and <see cref="Select"/> resolves a user-chosen preference with safe fallback.
/// When no engine is usable the chain yields an <see cref="UnavailableOcrEngine"/> that fails
/// per job (not at startup) with the full probe history.
/// </summary>
public sealed class OcrEngineChain
{
    private readonly object _gate = new();
    private readonly IOcrEngineFactory[] _factories;
    private readonly List<OcrEngineLink> _links = new();
    private IOcrEngine? _resolved;
    private int _next;

    public OcrEngineChain(IEnumerable<IOcrEngineFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories.ToArray();

        lock (_gate)
        {
            while (_next < _factories.Length && _resolved is null) ProbeOne();
            _resolved ??= new UnavailableOcrEngine(DescribeFailures(_links));
        }

        Engine = _resolved;
    }

    /// <summary>The first usable engine, or a failing stub when the whole chain is unavailable.</summary>
    public IOcrEngine Engine { get; }

    /// <summary>Registered factories in priority order; name and description read without probing.</summary>
    public IReadOnlyList<IOcrEngineFactory> Factories => _factories;

    /// <summary>Probe results so far, in chain order; stops at the first available engine until
    /// <see cref="EnsureAllProbed"/> / <see cref="Select"/> continues probing.</summary>
    public IReadOnlyList<OcrEngineLink> Links
    {
        get { lock (_gate) return _links.ToArray(); }
    }

    /// <summary>Whether a real engine was resolved (the stub does not count).</summary>
    public bool HasEngine => Links.Any(l => l.Available);

    /// <summary>
    /// Probes every remaining factory once, regardless of the engine already resolved at
    /// startup — this is what lets the settings page show each engine's availability.
    /// </summary>
    public void EnsureAllProbed()
    {
        lock (_gate)
        {
            while (_next < _factories.Length) ProbeOne();
        }
    }

    /// <summary>
    /// Resolves the user's preferred engine (by factory name), probing further factories only
    /// when the preference is not answered yet. An empty preference, an unknown name or an
    /// unusable engine falls back to the automatic priority chain (<see cref="Engine"/>), so a
    /// stale preference can never leave the app without a recogniser.
    /// </summary>
    public IOcrEngine Select(string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) return Engine;

        lock (_gate)
        {
            OcrEngineLink? match = FindAvailable(preferredName);
            while (match is null && _next < _factories.Length)
            {
                ProbeOne();
                OcrEngineLink last = _links[^1];
                if (last.Available && string.Equals(last.Name, preferredName, StringComparison.Ordinal))
                    match = last;
            }

            if (match?.Engine is not null) return match.Engine;
        }

        return Engine;
    }

    /// <summary>One line per probed link: "名称：可用" / "名称：不可用（原因）".</summary>
    public string Describe() =>
        string.Join(Environment.NewLine, Links.Select(DescribeLink));

    private OcrEngineLink? FindAvailable(string preferredName) =>
        _links.FirstOrDefault(l =>
            l.Available && string.Equals(l.Name, preferredName, StringComparison.Ordinal));

    /// <summary>Probes exactly one factory and records the outcome. Caller holds <see cref="_gate"/>.</summary>
    private void ProbeOne()
    {
        IOcrEngineFactory factory = _factories[_next++];
        try
        {
            if (factory.TryCreate(out IOcrEngine? engine, out string reason) && engine is not null)
            {
                _links.Add(new OcrEngineLink(factory.Name, engine, true, string.Empty));
                _resolved ??= engine;
            }
            else
            {
                _links.Add(new OcrEngineLink(factory.Name, null, false, reason));
            }
        }
        catch (Exception ex)
        {
            // A broken engine must not break the chain — record it and keep degrading.
            _links.Add(new OcrEngineLink(factory.Name, null, false, ex.Message));
        }
    }

    private static string DescribeLink(OcrEngineLink link) =>
        link.Available ? $"{link.Name}：可用" : $"{link.Name}：不可用（{link.Reason}）";

    private static string DescribeFailures(IReadOnlyList<OcrEngineLink> links) =>
        links.Count == 0
            ? "未注册任何 OCR 引擎"
            : string.Join("；", links.Select(l => $"{l.Name} 不可用（{l.Reason}）"));
}
