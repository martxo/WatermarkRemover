namespace WatermarkRemover.Scan.Core.Abstractions;

/// <summary>
/// Anything pluggable in the prototype implements this. Having a single identity contract is what
/// lets every extension point share one <c>ComponentRegistry&lt;T&gt;</c> and one settings/UI story:
/// add a new scan source, image processor, OCR engine, output target or naming profile by
/// implementing the matching interface and registering one instance.
/// </summary>
public interface IComponent
{
    /// <summary>Stable machine id, used in settings files and logs. Never localised.</summary>
    string Id { get; }

    /// <summary>Name shown in the UI.</summary>
    string DisplayName { get; }
}
