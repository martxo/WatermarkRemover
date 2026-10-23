namespace WatermarkRemover.Core.Features;

/// <summary>
/// One link of a probe chain: knows how to detect and create its product on this machine.
/// New products plug in by implementing this interface and registering in the chain —
/// selection, fallback and diagnostics follow automatically.
/// </summary>
public interface IProbeFactory<TProduct>
    where TProduct : class
{
    /// <summary>Human-readable name used in the settings page and chain reports.</summary>
    string Name { get; }

    /// <summary>One-line user-facing summary shown in the settings page.</summary>
    string Description => string.Empty;

    /// <summary>
    /// Attempts to create the product. Returns false with a human-readable reason when the
    /// product is unusable on this machine; the chain then falls through to the next link.
    /// </summary>
    bool TryCreate(out TProduct? product, out string reason);
}

/// <summary>Outcome of probing one chain link.</summary>
public sealed record ProbeLink<TProduct>(string Name, TProduct? Product, bool Available, string Reason)
    where TProduct : class;

/// <summary>
/// Resolves the best usable product by probing registered factories in priority order,
/// degrading safely: an unusable link falls through to the next, and <c>fallback</c> supplies the
/// product when the whole chain is unusable. Probing stops at the first usable link at startup
/// (cheap); <see cref="EnsureAllProbed"/> continues on demand when the settings page needs every
/// link's availability, and <see cref="Select"/> resolves a user-chosen preference with safe
/// fallback. The same probe result feeds runtime selection, settings and diagnostics, so the
/// three can never drift apart.
/// </summary>
public sealed class ProbeChain<TProduct>
    where TProduct : class
{
    private readonly object _gate = new();
    private readonly IProbeFactory<TProduct>[] _factories;
    private readonly List<ProbeLink<TProduct>> _links = new();
    private TProduct? _resolved;
    private int _next;

    public ProbeChain(
        IEnumerable<IProbeFactory<TProduct>> factories,
        Func<IReadOnlyList<ProbeLink<TProduct>>, TProduct> fallback)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(fallback);

        _factories = factories.ToArray();

        lock (_gate)
        {
            while (_next < _factories.Length && _resolved is null) ProbeOne();
            _resolved ??= fallback(_links.ToArray());
        }

        Product = _resolved;
    }

    /// <summary>The first usable product, or the fallback when nothing is usable.</summary>
    public TProduct Product { get; }

    /// <summary>Registered factories in priority order; name and description read without probing.</summary>
    public IReadOnlyList<IProbeFactory<TProduct>> Factories => _factories;

    /// <summary>Probe results so far, in chain order; stops at the first available link until
    /// <see cref="EnsureAllProbed"/> / <see cref="Select"/> continues probing.</summary>
    public IReadOnlyList<ProbeLink<TProduct>> Links
    {
        get { lock (_gate) return _links.ToArray(); }
    }

    /// <summary>Whether a real (non-fallback) product was resolved.</summary>
    public bool HasAvailable => Links.Any(l => l.Available);

    /// <summary>
    /// Probes every remaining factory once, regardless of the product already resolved at
    /// startup — this is what lets the settings page show each link's availability.
    /// </summary>
    public void EnsureAllProbed()
    {
        lock (_gate)
        {
            while (_next < _factories.Length) ProbeOne();
        }
    }

    /// <summary>
    /// Resolves the user's preferred product (by factory name), probing further factories only
    /// when the preference is not answered yet. An empty preference, an unknown name or an
    /// unusable link falls back to the automatic priority head, so a stale preference can never
    /// leave the app without a product.
    /// </summary>
    public TProduct Select(string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) return Product;

        lock (_gate)
        {
            ProbeLink<TProduct>? match = FindAvailable(preferredName);
            while (match is null && _next < _factories.Length)
            {
                ProbeOne();
                ProbeLink<TProduct> last = _links[^1];
                if (last.Available && string.Equals(last.Name, preferredName, StringComparison.Ordinal))
                    match = last;
            }

            if (match?.Product is not null) return match.Product;
        }

        return Product;
    }

    /// <summary>One line per probed link: "名称：可用" / "名称：不可用（原因）".</summary>
    public string Describe() =>
        string.Join(Environment.NewLine, Links.Select(DescribeLink));

    private ProbeLink<TProduct>? FindAvailable(string preferredName) =>
        _links.FirstOrDefault(l =>
            l.Available && string.Equals(l.Name, preferredName, StringComparison.Ordinal));

    /// <summary>Probes exactly one factory and records the outcome. Caller holds <see cref="_gate"/>.</summary>
    private void ProbeOne()
    {
        IProbeFactory<TProduct> factory = _factories[_next++];
        try
        {
            if (factory.TryCreate(out TProduct? product, out string reason) && product is not null)
            {
                _links.Add(new ProbeLink<TProduct>(factory.Name, product, true, string.Empty));
                _resolved ??= product;
            }
            else
            {
                _links.Add(new ProbeLink<TProduct>(factory.Name, null, false, reason));
            }
        }
        catch (Exception ex)
        {
            // A broken link must not break the chain — record it and keep degrading.
            _links.Add(new ProbeLink<TProduct>(factory.Name, null, false, ex.Message));
        }
    }

    private static string DescribeLink(ProbeLink<TProduct> link) =>
        link.Available ? $"{link.Name}：可用" : $"{link.Name}：不可用（{link.Reason}）";
}

/// <summary>Adapter for products whose detection is already expressed as a static probe.</summary>
public sealed class DelegateProbeFactory<TProduct> : IProbeFactory<TProduct>
    where TProduct : class
{
    private readonly Func<(bool Success, TProduct? Product, string Reason)> _probe;
    private readonly string _description;

    public DelegateProbeFactory(
        string name,
        Func<(bool, TProduct?, string)> probe,
        string description = "")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _description = description ?? string.Empty;
    }

    public string Name { get; }

    public string Description => _description;

    public bool TryCreate(out TProduct? product, out string reason)
    {
        (bool success, product, reason) = _probe();
        return success && product is not null;
    }
}
