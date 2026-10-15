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

/// <summary>
/// The expand/collapse chip shown at the end of the chip row when a job has more quick suffixes
/// than the visible limit. Pure presentation state — one instance per job.
/// </summary>
public sealed class ChipToggleViewModel : ObservableObject
{
    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => IsExpanded ? "收起" : "展开";
}
