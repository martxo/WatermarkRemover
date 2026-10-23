using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;
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
    private PdfPreviewViewModel? _strip;
    private bool _stripOpen;

    /// <summary>Which navigation section this record belongs to (scan / watermark / ocr…);
    /// the list is filtered per section so records never mix across pages.</summary>
    public string? OwnerFeatureId { get; internal set; }

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

    public string InputPath { get; private set; }

    public string FileName { get; private set; }

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
                OnPropertyChanged(nameof(ShowLiveStrip));
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

    /// <summary>A PDF output exists on disk, so the in-app preview can be offered for this row.</summary>
    public bool ShowPreview =>
        OutputPath is { } path &&
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Scans unfold their strip automatically — no button (the user asked for
    /// zero-click previews); every other section keeps the click-to-expand button.</summary>
    public Visibility PreviewButtonVisibility =>
        OwnerFeatureId == "scan.page" ? Visibility.Collapsed :
        ShowPreview ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The row's own thumbnail strip: the same preview view model the enlarge overlay later shows,
    /// created once on first expand and kept for the row's lifetime so renders, the rotation lock
    /// and the tile states stay identical in both surfaces.
    /// </summary>
    public PdfPreviewViewModel? Strip
    {
        get => _strip;
        private set => SetProperty(ref _strip, value);
    }

    /// <summary>Whether the strip under this row is expanded (the toggle the preview button drives).</summary>
    public bool StripOpen
    {
        get => _stripOpen;
        internal set
        {
            if (SetProperty(ref _stripOpen, value))
                OnPropertyChanged(nameof(PreviewToggleText));
        }
    }

    /// <summary>Preview button wording follows the strip: open it, or fold it away.</summary>
    public string PreviewToggleText => StripOpen ? "收起" : "预览";

    /// <summary>Pages that have arrived while the scanner is still feeding the document. The
    /// finished PDF strip takes over once the scan lands.</summary>
    public ObservableCollection<ImageSource> LiveThumbnails { get; } = new();

    /// <summary>Whether the live strip is visible: a processing scan row with at least one page.</summary>
    public bool ShowLiveStrip =>
        State == JobState.Processing &&
        OwnerFeatureId == "scan.page" &&
        LiveThumbnails.Count > 0;

    /// <summary>Adds one page thumbnail that arrived mid-scan (UI thread; late callbacks after
    /// finalisation are ignored).</summary>
    internal void AddLiveThumbnail(byte[] png)
    {
        if (State != JobState.Processing) return;

        try
        {
            LiveThumbnails.Add(DecodeThumbnail(png));
            OnPropertyChanged(nameof(ShowLiveStrip));
        }
        catch (Exception ex)
        {
            // A thumbnail is cosmetic — never let a decode failure abort the scan row.
            AppLog.Trace("扫描缩略图解码失败，已跳过该页预览", ex);
        }
    }

    /// <summary>Releases the live thumbnails once the real PDF strip is available.</summary>
    internal void ClearLiveThumbnails()
    {
        if (LiveThumbnails.Count == 0) return;

        LiveThumbnails.Clear();
        OnPropertyChanged(nameof(ShowLiveStrip));
    }

    private static ImageSource DecodeThumbnail(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Owner side (coordinator): attach a freshly built strip and expand it.</summary>
    internal void AttachStrip(PdfPreviewViewModel strip)
    {
        Strip = strip;
        StripOpen = true;
    }

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
                OnPropertyChanged(nameof(ShowPreview));
                OnPropertyChanged(nameof(PreviewButtonVisibility));
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

    public void MarkProcessing(string processingText)
    {
        State = JobState.Processing;
        Detail = processingText;
    }

    /// <summary>Live stage text while the row is processing (检测水印 / 识别文字 / 检查朝向 …).
    /// Called from queue worker threads, so it marshals to the UI thread; once the row has a
    /// terminal state, late stage callbacks are ignored.</summary>
    public void ReportStage(string stage)
    {
        if (State != JobState.Processing) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Detail = stage;
        }
        else
        {
            dispatcher.Invoke(() => Detail = stage);
        }
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
        DiagnosticReportBuilder.AppendEnvironmentHeader(report);
        report.AppendLine($"时间：{ProcessedAt:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"文件：{FileName}");
        if (!string.IsNullOrEmpty(_featureName))
            report.AppendLine($"功能：{_featureName}");
        report.AppendLine($"错误：{Detail}");
        report.AppendLine("---- 异常详情 ----");
        report.AppendLine(string.IsNullOrEmpty(_errorDetails) ? "（无异常详情）" : _errorDetails);
        DiagnosticReportBuilder.AppendEngineSection(report);

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

    /// <summary>
    /// Adopts the real file once an in-progress job (e.g. the scan placeholder, which starts
    /// without a path) has produced it. Keeps <see cref="FileName"/> consistent for the delete
    /// prompt and diagnostics.
    /// </summary>
    internal void AdoptSource(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
        OnPropertyChanged(nameof(InputPath));
        OnPropertyChanged(nameof(FileName));
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
