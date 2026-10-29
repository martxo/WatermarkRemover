using WatermarkRemover.App.Presentation;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One entry in the left navigation: a feature plus its icon and selection state.</summary>
public sealed class FeatureNavItemViewModel : ObservableObject
{
    private bool _isSelected;

    public FeatureNavItemViewModel(IFeature feature) =>
        Feature = feature ?? throw new ArgumentNullException(nameof(feature));

    public IFeature Feature { get; }

    /// <summary>Segoe MDL2 Assets glyph shown left of the feature name (presentation metadata).</summary>
    public string Glyph => FeaturePresentationCatalog.For(Feature).Glyph;

    public string DisplayName => Feature.DisplayName;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
