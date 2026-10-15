namespace WatermarkRemover.Core.Features;

/// <summary>
/// Ordered catalogue of the features available to the application. Selection per file is
/// first-match: registration order is priority order.
/// </summary>
public sealed class FeatureRegistry
{
    private readonly List<IFeature> _features = new();

    public FeatureRegistry(IEnumerable<IFeature> features)
    {
        if (features is null) throw new ArgumentNullException(nameof(features));

        foreach (var feature in features)
        {
            if (feature is null) throw new ArgumentException("Features must not be null.", nameof(features));
            if (_features.Any(f => string.Equals(f.Id, feature.Id, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Duplicate feature id '{feature.Id}'.", nameof(features));
            _features.Add(feature);
        }

        if (_features.Count == 0)
            throw new ArgumentException("At least one feature is required.", nameof(features));
    }

    public IReadOnlyList<IFeature> Features => _features;

    /// <summary>The first feature that can process this file, or null when the type is unsupported.</summary>
    public IFeature? FindForFile(string filePath) =>
        _features.FirstOrDefault(f => f.CanHandle(filePath));
}
