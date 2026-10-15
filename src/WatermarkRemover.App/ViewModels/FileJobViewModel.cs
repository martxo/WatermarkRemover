using System.Collections.ObjectModel;
using System.IO;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.ViewModels;

public enum JobState
{
    Queued,
    Processing,
    Succeeded,
    Skipped,
    Failed,
}

/// <summary>One input file in the processing queue.</summary>
public sealed class FileJobViewModel : ObservableObject
{
    /// <summary>How many quick-suffix chips are shown before the expand chevron appears.</summary>
    private const int MaxVisibleChips = 4;

    private JobState _state = JobState.Queued;
    private string _detail = "等待处理";
    private string? _outputPath;
    private int _watermarkCount;
    private DateTime? _processedAt;
    private string _displayName;
    private bool _isEditing;
    private string _editText = string.Empty;
    private ObservableCollection<SuffixChipViewModel> _suffixChips = new();
    private ObservableCollection<SuffixChipViewModel> _inlineChips = new();
    private ObservableCollection<SuffixChipViewModel> _overflowChips = new();
    private bool _isSuffixDropdownOpen;
    private long _closedByOutsideClickTick;
    private bool _closedByOutsideClickArmed;
    private bool _suppressCloseStamp;

    public FileJobViewModel(string inputPath, IReadOnlyList<string> quickSuffixes)
    {
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
        _displayName = FileName;
        RefreshSuffixChips(quickSuffixes);

        BeginEditCommand = new RelayCommand(BeginEdit, () => IsRenamable && !IsEditing);
        CommitEditCommand = new RelayCommand(CommitEdit, () => IsEditing);
        CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        ToggleSuffixCommand = new RelayCommand(p => ToggleSuffix(p as SuffixChipViewModel), p => IsRenamable && !IsEditing);
        ToggleSuffixDropdownCommand = new RelayCommand(ToggleSuffixDropdown, () => IsRenamable);
    }

    /// <summary>Raised when the user commits an edited name. The coordinator performs the move.</summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>Raised when a quick-suffix chip is clicked; carries the suffix label.</summary>
    public event EventHandler<string>? SuffixToggleRequested;

    public string InputPath { get; }

    public string FileName { get; }

    /// <summary>
    /// Name shown on the card: the output file's name once generated (that is the file the user
    /// manages), the input name before that.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (SetProperty(ref _displayName, value))
                RefreshChipStates();
        }
    }

    public JobState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => State switch
    {
        JobState.Queued => "待处理",
        JobState.Processing => "处理中",
        JobState.Succeeded => "完成",
        JobState.Skipped => "无水印",
        JobState.Failed => "失败",
        _ => State.ToString(),
    };

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(IsRenamable));
                OnPropertyChanged(nameof(ChipsVisible));
            }
        }
    }

    /// <summary>A generated output file exists and can be renamed by the user.</summary>
    public bool IsRenamable => OutputPath != null;

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
                OnPropertyChanged(nameof(HeaderToolTip));
        }
    }

    /// <summary>Two-way bound buffer for the inline rename box.</summary>
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    public ObservableCollection<SuffixChipViewModel> SuffixChips => _suffixChips;

    /// <summary>The first <see cref="MaxVisibleChips"/> suffixes shown directly on the card.</summary>
    public ObservableCollection<SuffixChipViewModel> InlineChips => _inlineChips;

    /// <summary>Suffixes beyond the inline limit — the only ones the dropdown overlay shows.</summary>
    public ObservableCollection<SuffixChipViewModel> OverflowChips => _overflowChips;

    /// <summary>
    /// Whether the suffix dropdown overlay is shown. The Popup writes false back through this
    /// setter when the user clicks outside it (StaysOpen=False), keeping the VM in sync.
    /// </summary>
    public bool IsSuffixDropdownOpen
    {
        get => _isSuffixDropdownOpen;
        set
        {
            if (SetProperty(ref _isSuffixDropdownOpen, value))
            {
                OnPropertyChanged(nameof(HasMoreChips));
                if (!value)
                {
                    // Remember outside-click closes: the stray Click the toggle button receives for
                    // that same click must not instantly reopen the dropdown (flicker).
                    if (_suppressCloseStamp)
                    {
                        _suppressCloseStamp = false;
                        _closedByOutsideClickArmed = false;
                    }
                    else
                    {
                        _closedByOutsideClickArmed = true;
                        _closedByOutsideClickTick = Environment.TickCount64;
                    }
                }
            }
        }
    }

    /// <summary>More suffixes exist than the inline row can show, so the chevron toggle is visible.</summary>
    public bool HasMoreChips => _suffixChips.Count > MaxVisibleChips;

    /// <summary>Quick suffixes are only useful once the output file exists.</summary>
    public bool ChipsVisible => IsRenamable && _suffixChips.Count > 0;

    public string HeaderToolTip =>
        IsRenamable ? "点击文件名可重命名" : "处理完成后点击文件名可重命名";

    public RelayCommand BeginEditCommand { get; }

    public RelayCommand CommitEditCommand { get; }

    public RelayCommand CancelEditCommand { get; }

    public RelayCommand ToggleSuffixCommand { get; }

    public RelayCommand ToggleSuffixDropdownCommand { get; }

    public int WatermarkCount
    {
        get => _watermarkCount;
        private set => SetProperty(ref _watermarkCount, value);
    }

    /// <summary>Time the job reached a terminal state (done, skipped, or failed).</summary>
    public DateTime? ProcessedAt
    {
        get => _processedAt;
        private set
        {
            if (SetProperty(ref _processedAt, value))
                OnPropertyChanged(nameof(ProcessedAtText));
        }
    }

    public string ProcessedAtText => ProcessedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;

    public void MarkProcessing()
    {
        State = JobState.Processing;
        Detail = "正在分析并去除水印…";
    }

    public void Apply(FeatureResult result)
    {
        ProcessedAt = DateTime.Now;
        switch (result.Status)
        {
            case FeatureStatus.Succeeded:
                string outputPath = result.OutputPath ?? string.Empty;
                State = JobState.Succeeded;
                OutputPath = outputPath;
                WatermarkCount = result.ItemCount;
                DisplayName = Path.GetFileName(outputPath);
                Detail = $"{result.Summary} → {outputPath}";
                break;

            case FeatureStatus.NoChange:
                State = JobState.Skipped;
                Detail = $"{result.Summary}，已跳过";
                break;

            case FeatureStatus.Failed:
                State = JobState.Failed;
                Detail = result.Error ?? "处理失败";
                break;
        }
    }

    /// <summary>Called by the coordinator after the output file was successfully moved.</summary>
    public void UpdateRenamed(string newPath)
    {
        OutputPath = newPath;
        DisplayName = Path.GetFileName(newPath);
        Detail = $"已去除 {WatermarkCount} 处水印 → {newPath}";
        RefreshChipStates();
    }

    /// <summary>Rebuilds the chip row from the (possibly changed) quick-suffix settings.</summary>
    public void RefreshSuffixChips(IReadOnlyList<string> quickSuffixes)
    {
        _suffixChips.Clear();
        foreach (string suffix in quickSuffixes.Where(s => !string.IsNullOrWhiteSpace(s)))
            _suffixChips.Add(new SuffixChipViewModel(suffix.Trim()));

        _inlineChips.Clear();
        for (int i = 0; i < _suffixChips.Count && i < MaxVisibleChips; i++)
            _inlineChips.Add(_suffixChips[i]);

        _overflowChips.Clear();
        for (int i = MaxVisibleChips; i < _suffixChips.Count; i++)
            _overflowChips.Add(_suffixChips[i]);

        RefreshChipStates();
        OnPropertyChanged(nameof(HasMoreChips));
        OnPropertyChanged(nameof(ChipsVisible));
    }

    /// <summary>
    /// Opens the suffix dropdown; closing happens via the Popup (outside click) or a chip click.
    /// A close from an outside click is immediately followed by the toggle button's stray Click —
    /// swallow that one so the dropdown does not flicker closed-then-open.
    /// </summary>
    private void ToggleSuffixDropdown()
    {
        if (!IsSuffixDropdownOpen && _closedByOutsideClickArmed && Environment.TickCount64 - _closedByOutsideClickTick < 300)
            return;

        _closedByOutsideClickArmed = false;
        IsSuffixDropdownOpen = !IsSuffixDropdownOpen;
    }

    private void RefreshChipStates()
    {
        foreach (var chip in _suffixChips)
            chip.IsApplied = OutputNameEditor.IsSuffixApplied(DisplayName, chip.Label);
    }

    private void BeginEdit()
    {
        if (!IsRenamable || IsEditing) return;
        EditText = DisplayName;
        IsEditing = true;
    }

    private void CommitEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;

        string desired = EditText.Trim();
        if (desired.Length == 0 || string.Equals(desired, DisplayName, StringComparison.OrdinalIgnoreCase))
            return;

        RenameRequested?.Invoke(this, desired);
    }

    private void CancelEdit() => IsEditing = false;

    private void ToggleSuffix(SuffixChipViewModel? chip)
    {
        if (chip is null || !IsRenamable || IsEditing) return;
        SuffixToggleRequested?.Invoke(this, chip.Label);

        // A dropdown menu item applies and dismisses — standard dropdown behaviour.
        CloseSuffixDropdown();
    }

    /// <summary>Closes the suffix dropdown from within the view model (chip applied).</summary>
    private void CloseSuffixDropdown()
    {
        // VM-initiated close: not an outside click, so do not arm the flicker guard.
        _suppressCloseStamp = true;
        IsSuffixDropdownOpen = false;
    }
}
