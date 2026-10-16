using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Updates;
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
    private string? _errorDetails;
    private string? _featureName;
    private int _itemCount;
    private string _resultSummary = "已处理";
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
        CopyErrorCommand = new RelayCommand(CopyErrorReport, _ => State == JobState.Failed);
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
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(ShowStatusChip));
                OnPropertyChanged(nameof(ShowCopyError));
            }
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

    /// <summary>
    /// Queued/processing/failed carry real signal, so the status chip shows. Terminal states
    /// (done / skipped) say nothing the user needs — the delete button takes that spot instead.
    /// </summary>
    public bool ShowStatusChip => State is JobState.Queued or JobState.Processing or JobState.Failed;

    /// <summary>Only a failed job has a diagnostic worth copying.</summary>
    public bool ShowCopyError => State == JobState.Failed;

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

    /// <summary>Copies a full diagnostic report of this failure to the clipboard for bug reports.</summary>
    public RelayCommand CopyErrorCommand { get; }

    /// <summary>How many discrete items the feature acted on (watermarks removed, pages recognised…).</summary>
    public int ItemCount
    {
        get => _itemCount;
        private set => SetProperty(ref _itemCount, value);
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
        _errorDetails = result.ErrorDetails;
        _featureName = result.FeatureName;
        switch (result.Status)
        {
            case FeatureStatus.Succeeded:
                string outputPath = result.OutputPath ?? string.Empty;
                State = JobState.Succeeded;
                OutputPath = outputPath;
                ItemCount = result.ItemCount;
                _resultSummary = string.IsNullOrWhiteSpace(result.Summary) ? "已处理" : result.Summary;
                DisplayName = Path.GetFileName(outputPath);
                Detail = $"{_resultSummary} → {outputPath}";
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

    /// <summary>
    /// Puts a self-contained failure report on the clipboard: app/OS context, the file and feature
    /// involved, and the full exception dump. A user pastes it straight into a bug report, which is
    /// enough to diagnose the machine remotely (engine chain, HRESULTs, stack traces).
    /// </summary>
    private void CopyErrorReport(object? parameter)
    {
        var report = new StringBuilder();
        report.AppendLine($"应用版本：v{UpdateService.CurrentVersion}");
        report.AppendLine($"系统：{Environment.OSVersion.VersionString}");
        report.AppendLine($"进程：{(Environment.Is64BitProcess ? "64" : "32")} 位（{(Environment.Is64BitOperatingSystem ? "64" : "32")} 位系统）");
        report.AppendLine($"时间：{ProcessedAt:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"文件：{FileName}");
        if (!string.IsNullOrEmpty(_featureName))
            report.AppendLine($"功能：{_featureName}");
        report.AppendLine($"错误：{Detail}");
        report.AppendLine("---- 异常详情 ----");
        report.AppendLine(string.IsNullOrEmpty(_errorDetails) ? "（无异常详情）" : _errorDetails);
        report.AppendLine("---- OCR 引擎 ----");
        report.AppendLine(OcrDiagnostics.Current);

        try
        {
            Clipboard.SetText(report.ToString());
            Detail = "报错信息已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Detail = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>Called by the coordinator after the output file was successfully moved.</summary>
    public void UpdateRenamed(string newPath)
    {
        OutputPath = newPath;
        DisplayName = Path.GetFileName(newPath);
        Detail = $"{_resultSummary} → {newPath}";
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
