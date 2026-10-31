using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
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

/// <summary>Netdisk upload state of one finished record. The row is the only place the user can
/// see whether a file actually reached the cloud, so the state lives here, not in a log.</summary>
public enum UploadState
{
    None,
    Uploading,
    Uploaded,
    Failed,
}

/// <summary>One input file in the processing queue.</summary>
public sealed class FileJobViewModel : ObservableObject
{
    /// <summary>How many quick-suffix chips are shown before the expand chevron appears.</summary>
    private const int MaxVisibleChips = 4;

    private JobState _state = JobState.Queued;
    private string _detail = "等待处理";
    private string? _namingNote;
    private UploadState _upload;
    private string? _uploadNote;
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
    private bool _canContinueScan;
    private ImageSource? _thumbnail;

    /// <summary>Numbered-rename explanations expire by the app's shared transient rule.</summary>
    private readonly TransientStatus _transientNote;

    /// <summary>Which navigation section this record belongs to (scan / watermark / ocr…);
    /// the list is filtered per section so records never mix across pages.</summary>
    public string? OwnerFeatureId { get; internal set; }

    public FileJobViewModel(string inputPath, IReadOnlyList<string> quickSuffixes)
    {
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
        _displayName = FileName;
        _transientNote = new TransientStatus(() => NamingNote, text => SetNamingNote(text));
        RefreshSuffixChips(quickSuffixes);
        BeginEditCommand = new RelayCommand(BeginEdit, () => IsRenamable && !IsEditing);
        CommitEditCommand = new RelayCommand(CommitEdit, () => IsEditing);
        CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        ToggleSuffixCommand = new RelayCommand(p => ToggleSuffix(p as SuffixChipViewModel), p => IsRenamable && !IsEditing);
        ToggleSuffixDropdownCommand = new RelayCommand(ToggleSuffixDropdown, () => IsRenamable);
        CopyErrorCommand = new RelayCommand(CopyErrorReport, _ => State == JobState.Failed);
        RetryCommand = new RelayCommand(_ => RetryRequested?.Invoke(this, EventArgs.Empty), _ => ShowRetry);
        UploadCommand = new RelayCommand(_ => UploadRequested?.Invoke(this, EventArgs.Empty), _ => ShowUploadButton);
        ContinueScanCommand = new RelayCommand(
            _ => ContinueScanRequested?.Invoke(this, EventArgs.Empty), _ => ShowContinueScan);
        CopyImageCommand = new RelayCommand(_ => CopyImage(), _ => ShowScreenshotActions);
        OpenImageCommand = new RelayCommand(_ => OpenImage(), _ => ShowScreenshotActions);
    }

    /// <summary>Raised when the user asks to run this failed scan again; the host owns the scanner.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>Raised when the user asks to append another scanned batch to this row's PDF;
    /// the host owns the scanner and the file write.</summary>
    public event EventHandler? ContinueScanRequested;

    /// <summary>Raised when the user asks to send this row's file to the netdisk; the host owns the account.</summary>
    public event EventHandler? UploadRequested;

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
                OnPropertyChanged(nameof(ShowCopyError));
                OnPropertyChanged(nameof(ShowRetry));
                OnPropertyChanged(nameof(ShowContinueScan));
                OnPropertyChanged(nameof(ShowLiveStrip));
                OnPropertyChanged(nameof(ShowUploadChip));
                OnPropertyChanged(nameof(ShowUploadButton));
                OnPropertyChanged(nameof(ShowScreenshotActions));
                OnPropertyChanged(nameof(StatusLine));
            }
        }
    }

    /// <summary>
    /// The row's single status line: a failure (and the stage/detail text) outranks the naming
    /// note, which in turn outranks the normal detail. One line, one message — the old row showed
    /// a separate status chip, a detail line and a naming line all at once.
    /// </summary>
    public string StatusLine =>
        State == JobState.Failed ? Detail
        : NamingNote is { Length: > 0 } note ? note
        : Detail;

    /// <summary>Only a failed job has a diagnostic worth copying.</summary>
    public bool ShowCopyError => State == JobState.Failed;

    /// <summary>A failed scan can simply be re-run — there is no source file to reprocess, so the
    /// generic "drop the file again" recovery does not exist for it.</summary>
    public bool ShowRetry => State == JobState.Failed && OwnerFeatureId == "scan.page";

    /// <summary>An interrupted or cancelled scan kept its pages, so the row offers
    /// 「继续扫描」: another batch is appended to this same PDF instead of starting a new file.
    /// Only rows the host marked as continuable show it (restored / untouched scans do not).</summary>
    public bool ShowContinueScan =>
        _canContinueScan && State == JobState.Succeeded &&
        OutputPath is { } path && File.Exists(path);

    /// <summary>A PDF output exists on disk, so the in-app preview can be offered for this row.</summary>
    public bool ShowPreview =>
        OutputPath is { } path &&
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Screenshot rows behave like a light Tendedero shelf: a thumbnail, click-to-copy, drag the
    /// file into another app, and open the original. Only image outputs get these actions.
    /// </summary>
    public bool ShowScreenshotActions =>
        OwnerFeatureId == "screenshot.page" &&
        State == JobState.Succeeded &&
        IsImagePath(OutputPath);

    /// <summary>Can this row's file be dragged out of the app (screenshot image only)?</summary>
    public bool CanDragScreenshot => ShowScreenshotActions && OutputPath is not null;

    /// <summary>Small decoded preview for screenshot rows (null for every other output type).</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public RelayCommand CopyImageCommand { get; }

    public RelayCommand OpenImageCommand { get; }

    private static bool IsImagePath(string? path) => path is not null &&
        Path.GetExtension(path) is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif";

    /// <summary>Builds a frozen 160px-wide thumbnail; a broken image never breaks the row.</summary>
    private void LoadThumbnail(string? path)
    {
        if (!IsImagePath(path) || !File.Exists(path))
        {
            Thumbnail = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.UriSource = new Uri(path!);
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
        }
        catch (Exception ex)
        {
            Thumbnail = null;
            AppLog.Trace("截图缩略图解码失败", ex);
        }
    }

    private void CopyImage()
    {
        if (OutputPath is not { } path || !File.Exists(path))
        {
            ShowTransientNameNote("图片已不存在，无法复制");
            return;
        }

        try
        {
            var image = new BitmapImage();
            using (var stream = File.OpenRead(path))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }

            image.Freeze();
            Clipboard.SetImage(image);
            ShowTransientNameNote("图片已复制到剪贴板");
        }
        catch (Exception ex)
        {
            ShowTransientNameNote($"复制图片失败：{ex.Message}");
        }
    }

    private void OpenImage()
    {
        if (OutputPath is not { } path || !File.Exists(path))
        {
            ShowTransientNameNote("图片已不存在，无法打开");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowTransientNameNote($"打开图片失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Every finished row — scans included — keeps a 预览/收起 button. The row that just finished
    /// unfolds its strip on its own; older rows stay folded, and a fold hands the tiles back.
    /// </summary>
    public Visibility PreviewButtonVisibility =>
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

    /// <summary>Owner side (coordinator): attach a freshly built strip and unfold it.</summary>
    internal void AttachStrip(PdfPreviewViewModel strip)
    {
        Strip = strip;
        StripOpen = true;
    }

    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>
    /// Background naming is invisible by design (the row is already "完成" under a timestamp
    /// name). This one line is its whole surface: what it is doing, or why the name stayed.
    /// While it has text it owns <see cref="StatusLine"/>.
    /// </summary>
    public string? NamingNote
    {
        get => _namingNote;
        private set
        {
            if (SetProperty(ref _namingNote, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>Explains a numbered rename for a few seconds, using the app's shared expiry rule.</summary>
    public void ShowTransientNameNote(string note) => _transientNote.Show(note);

    /// <summary>Sets the naming line from any thread — naming runs off the UI thread.</summary>
    public void SetNamingNote(string? note)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            NamingNote = note;
        else
            dispatcher.Invoke(() => NamingNote = note);
    }

    /// <summary>Where this record's file stands with the netdisk; surfaced as the row chip.</summary>
    public UploadState Upload
    {
        get => _upload;
        private set
        {
            if (SetProperty(ref _upload, value))
            {
                OnPropertyChanged(nameof(UploadChipText));
                OnPropertyChanged(nameof(UploadTip));
                OnPropertyChanged(nameof(ShowUploadChip));
                OnPropertyChanged(nameof(ShowUploadButton));
                UploadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Why the upload failed, or which account it landed on — shown in the chip tooltip.</summary>
    public string? UploadNote
    {
        get => _uploadNote;
        private set
        {
            if (SetProperty(ref _uploadNote, value))
                OnPropertyChanged(nameof(UploadTip));
        }
    }

    /// <summary>「已上传」 stays on the record; a failed or in-flight upload keeps saying so.</summary>
    public bool ShowUploadChip => State == JobState.Succeeded && Upload != UploadState.None;

    /// <summary>The upload action hides once the file is in the cloud; during an upload the
    /// button disappears too, so a second click cannot start a parallel transfer.</summary>
    public bool ShowUploadButton =>
        State == JobState.Succeeded && Upload is not (UploadState.Uploaded or UploadState.Uploading);

    public string UploadChipText => Upload switch
    {
        UploadState.Uploading => "上传中…",
        UploadState.Uploaded => "已上传",
        UploadState.Failed => "上传失败",
        _ => string.Empty,
    };

    public string UploadTip => UploadNote is { Length: > 0 } note
        ? note
        : Upload switch
        {
            UploadState.Uploaded => "这个文件已在网盘里",
            UploadState.Uploading => "正在上传到网盘",
            UploadState.Failed => "上传失败，点击重试",
            _ => "把这个文件上传到网盘",
        };

    /// <summary>Sets the upload state from any thread — netdisk transfers run off the UI thread.</summary>
    public void SetUploadState(UploadState state, string? note = null)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UploadNote = note;
            Upload = state;
        }
        else
        {
            dispatcher.Invoke(() =>
            {
                UploadNote = note;
                Upload = state;
            });
        }
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
                OnPropertyChanged(nameof(ShowContinueScan));
                OnPropertyChanged(nameof(PreviewButtonVisibility));
                OnPropertyChanged(nameof(ShowScreenshotActions));
                OnPropertyChanged(nameof(HasThumbnail));
                CopyImageCommand.RaiseCanExecuteChanged();
                OpenImageCommand.RaiseCanExecuteChanged();
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

    /// <summary>Re-runs the scan for this failed row (host handles the scanner session).</summary>
    public RelayCommand RetryCommand { get; }

    /// <summary>Appends another scanned batch to this row's PDF (host handles the scanner).</summary>
    public RelayCommand ContinueScanCommand { get; }

    /// <summary>Sends this row's file to the netdisk (host owns the configured account).</summary>
    public RelayCommand UploadCommand { get; }

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

    /// <summary>Host side: this scan kept a partial document, so 「继续扫描」 becomes available.</summary>
    internal void AllowContinueScan()
    {
        if (_canContinueScan) return;
        _canContinueScan = true;
        OnPropertyChanged(nameof(ShowContinueScan));
        ContinueScanCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Progress / result line for a continuation pass. The row is already Succeeded, so
    /// <see cref="ReportStage"/> would ignore it; this writes the same line without that gate.</summary>
    public void ReportContinuation(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Detail = text;
        }
        else
        {
            dispatcher.Invoke(() => Detail = text);
        }
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
                LoadThumbnail(outputPath);
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

    /// <summary>
    /// Rebuilds a finished record read back from the task journal (app restart) under its original
    /// finish time. Deliberately not <see cref="Apply"/>: that stamps <c>DateTime.Now</c> and would
    /// make yesterday's scans look like today's.
    /// </summary>
    internal void Restore(
        string inputPath, string outputPath, string summary, int itemCount, DateTime processedAt)
    {
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
        OnPropertyChanged(nameof(InputPath));
        OnPropertyChanged(nameof(FileName));

        ProcessedAt = processedAt;
        State = JobState.Succeeded;
        OutputPath = outputPath;
        ItemCount = itemCount;
        _resultSummary = string.IsNullOrWhiteSpace(summary) ? "已处理" : summary;
        DisplayName = Path.GetFileName(outputPath);
        Detail = $"{_resultSummary} → {outputPath}";
        LoadThumbnail(outputPath);
        RefreshChipStates();
    }

    /// <summary>What lands in the task journal for this row — the fields <see cref="Restore"/>
    /// reads back, so the two stay in lockstep.</summary>
    internal TaskJournalEntry ToJournalEntry() => new(
        OwnerFeatureId ?? string.Empty,
        InputPath,
        OutputPath ?? string.Empty,
        _resultSummary,
        ItemCount,
        ProcessedAt ?? DateTime.Now,
        Upload.ToString(),
        UploadNote);

    /// <summary>Called by the coordinator after the output file was successfully moved.</summary>
    public void UpdateRenamed(string newPath)
    {
        OutputPath = newPath;
        DisplayName = Path.GetFileName(newPath);
        Detail = $"{_resultSummary} → {newPath}";
        LoadThumbnail(newPath);
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
