namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// One link of the layout stage chain: knows how to detect and create its block on this machine.
/// New blocks plug in by implementing this interface and registering in the chain — settings UI,
/// selection and fallback follow automatically.
/// </summary>
public interface ILayoutStageFactory
{
    /// <summary>Human-readable block name used in the settings page and chain reports.</summary>
    string Name { get; }

    /// <summary>One-line user-facing summary shown on the settings OCR section.</summary>
    string Description => string.Empty;

    /// <summary>
    /// Attempts to create the block. Returns false with a human-readable reason when the block
    /// is unusable on this machine; the chain then falls through to the next link.
    /// </summary>
    bool TryCreate(out ILayoutStage? stage, out string reason);
}

/// <summary>Adapter for blocks whose detection is already expressed as a static probe.</summary>
public sealed class DelegateLayoutStageFactory : ILayoutStageFactory
{
    private readonly Func<(bool Success, ILayoutStage? Stage, string Reason)> _probe;
    private readonly string _description;

    public DelegateLayoutStageFactory(string name, Func<(bool, ILayoutStage?, string)> probe, string description = "")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _description = description ?? string.Empty;
    }

    public string Name { get; }

    public string Description => _description;

    public bool TryCreate(out ILayoutStage? stage, out string reason)
    {
        (bool success, stage, reason) = _probe();
        return success && stage is not null;
    }
}
