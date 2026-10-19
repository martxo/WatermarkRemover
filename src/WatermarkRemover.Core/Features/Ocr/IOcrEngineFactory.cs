namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// One link of the OCR engine chain: knows how to detect and create its engine on this machine.
/// New engines plug in by implementing this interface and registering in the chain — selection,
/// fallback and diagnostics follow automatically.
/// </summary>
public interface IOcrEngineFactory
{
    /// <summary>Human-readable engine name used in chain reports (e.g. 微信 OCR).</summary>
    string Name { get; }

    /// <summary>One-line user-facing summary shown on the settings OCR section.</summary>
    string Description => string.Empty;

    /// <summary>
    /// Attempts to create the engine. Returns false with a human-readable reason when the
    /// engine is unusable on this machine; the chain then falls through to the next link.
    /// </summary>
    bool TryCreate(out IOcrEngine? engine, out string reason);
}

/// <summary>Adapter for engines whose detection is already expressed as a static TryLocate.</summary>
public sealed class DelegateOcrEngineFactory : IOcrEngineFactory
{
    private readonly Func<(bool Success, IOcrEngine? Engine, string Reason)> _probe;
    private readonly string _description;

    public DelegateOcrEngineFactory(string name, Func<(bool, IOcrEngine?, string)> probe, string description = "")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _description = description ?? string.Empty;
    }

    public string Name { get; }

    public string Description => _description;

    public bool TryCreate(out IOcrEngine? engine, out string reason)
    {
        (bool success, engine, reason) = _probe();
        return success && engine is not null;
    }
}
