using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WatermarkRemover.Core.Processing;
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
    private JobState _state = JobState.Queued;
    private string _detail = "等待处理";
    private string? _outputPath;
    private int _watermarkCount;
    private DateTime? _processedAt;
    private string _displayName;
    private bool _isEditing;
    private string _editText = string.Empty;
    private ObservableCollection<SuffixChipViewModel> _suffixChips = new();

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

    /// <summary>Quick suffixes are only useful once the output file exists.</summary>
    public bool ChipsVisible => IsRenamable && _suffixChips.Count > 0;

    public string HeaderToolTip =>
        IsRenamable ? "点击文件名可重命名" : "处理完成后点击文件名可重命名";

    public RelayCommand BeginEditCommand { get; }

    public RelayCommand CommitEditCommand { get; }

    public RelayCommand CancelEditCommand { get; }

    public RelayCommand ToggleSuffixCommand { get; }

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

    public void Apply(ProcessingResult result)
    {
        ProcessedAt = DateTime.Now;
        switch (result.Status)
        {
            case ProcessingStatus.Succeeded:
                string outputPath = result.OutputPath ?? string.Empty;
                State = JobState.Succeeded;
                OutputPath = outputPath;
                WatermarkCount = result.Marks.Count;
                DisplayName = Path.GetFileName(outputPath);
                Detail = $"已去除 {result.Marks.Count} 处水印 → {outputPath}";
                break;

            case ProcessingStatus.NoWatermarkFound:
                State = JobState.Skipped;
                Detail = "未检测到水印，已跳过";
                break;

            case ProcessingStatus.Failed:
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

        RefreshChipStates();
        OnPropertyChanged(nameof(ChipsVisible));
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
    }
}
