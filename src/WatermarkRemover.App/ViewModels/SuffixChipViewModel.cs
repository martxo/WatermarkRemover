namespace WatermarkRemover.App.ViewModels;

/// <summary>One quick-suffix chip under a file name; lights up when the name ends with it.</summary>
public sealed class SuffixChipViewModel : ObservableObject
{
    private bool _isApplied;

    public SuffixChipViewModel(string label) => Label = label;

    public string Label { get; }

    public bool IsApplied
    {
        get => _isApplied;
        set => SetProperty(ref _isApplied, value);
    }
}
