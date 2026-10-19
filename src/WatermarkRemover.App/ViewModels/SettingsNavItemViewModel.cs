namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One entry of the settings page navigation (mirrors the feature nav pattern of
/// <see cref="FeatureNavItemViewModel"/>): registering an item in
/// <see cref="SettingsViewModel.NavItems"/> is all it takes to add a new settings section —
/// the sidebar, the search filter and the section host pick it up automatically.
/// </summary>
public sealed class SettingsNavItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;

    /// <param name="id">Stable key the section host matches on (its ContentControl style trigger).</param>
    /// <param name="title">Header shown in the nav row and as the section title.</param>
    /// <param name="glyph">Segoe MDL2 icon for the nav row.</param>
    /// <param name="keywords">Extra search terms matched (besides the title) by the nav search box.</param>
    public SettingsNavItemViewModel(string id, string title, string glyph, string keywords)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        Keywords = keywords;
    }

    public string Id { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string Keywords { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>False while the nav search box filters this row out.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>True when the query is empty or matches the title / keywords.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase);
}
