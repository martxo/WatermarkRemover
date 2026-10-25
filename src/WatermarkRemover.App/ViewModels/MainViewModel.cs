using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Index;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// Drop-zone visual state, following the three-state pattern mainstream upload components use
/// (Ant Design Dragger / react-dropzone): idle at rest, accept while a supported file hovers,
/// reject when the payload can't be handled. The view renders purely from this state.
/// </summary>
public enum DropZoneState
{
    Idle,
    Accept,
    Reject,
}

/// <summary>
/// Root view model. Dropping files triggers the selected feature immediately; results are pushed
/// into the observable queue as each job finishes. This class holds no feature logic of its own.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>One shared engine: the WeChat OCR worker is a single channel, so both features
    /// must go through the same instance to keep recognition serialised. The instance is a
    /// stable proxy — the settings OCR section swaps its target at runtime.</summary>
    private static readonly IOcrEngine SharedOcrEngine = CreateOcrEngine();

    /// <summary>One shared PDF renderer: turning pages into images is stateless, so the OCR
    /// features and the preview overlay all go through the same instance.</summary>
    private static readonly IPdfPageRenderer SharedPdfRenderer = new PdfiumPdfPageRenderer();

    private readonly FeatureRegistry _registry = new(new IFeature[]
    {
        // Scan first: the app opens straight onto the scan page, so a walk-up user can scan
        // without touching navigation. Registration order drives both the sidebar and the
        // default selection (NavItems[0] below).
        new ScanFeature(),
        // 快速搜索 sits next to the default page: it is a tool, not a processor (CanHandle=false),
        // so it never interferes with first-match routing in FindForFile.
        new QuickSearchFeature(),
        new WatermarkFeature(
            new PdfDocumentSource(),
            new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            SharedPdfRenderer,
            SharedOcrEngine,
            orientPage: image => new PdfOrientationDetector(SharedOcrEngine).OrientAsync(image),
            trace: AppLog.Trace),
        new OcrFeature(SharedPdfRenderer, SharedOcrEngine, trace: AppLog.Trace,
            imageLoader: ImageOcrInput.LoadAsOcrPng),
        new NetdiskFeature(),
        new LanTransferFeature(),
    });

    private IFeature _selectedFeature;
    private readonly IDialogService _dialogs;
    private readonly IShellOpenService _shell;
    private readonly TaskJournal? _journal;
    private int _journalSaveQueued;
    private readonly JobQueueService _queue = new();
    private readonly ScanCoordinator _scan;
    private readonly ContentNamingService _naming;
    private readonly JobRenamer _renamer;
    private readonly PreviewStripCoordinator _preview;
    private bool _isBusy;
    private bool _isScanning;
    private bool _isSettingsOpen;
    private SettingsViewModel? _settingsPage;

    public MainViewModel()
        : this(DialogService.Default, ShellOpenService.Default,
            new TaskJournal(Path.Combine(AppFolders.Root, "journal")))
    {
    }

    /// <summary>Service-injected constructor; tests drive the UI surface through fakes. The journal
    /// is optional so a test run never reads or writes the machine's real job history.</summary>
    internal MainViewModel(IDialogService dialogs, IShellOpenService shell, TaskJournal? journal = null)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _journal = journal;

        Settings = SettingsService.Load();
        // Honour the pipeline pinned in settings (empty = automatic priority chains).
        AppOcrEngineChain.ApplyPipeline(Settings.PreferredLayoutStage, Settings.PreferredOcrEngine);
        Board = new JobBoard(_dialogs);
        NavItems = new ObservableCollection<FeatureNavItemViewModel>(
            _registry.Features.Select(f => new FeatureNavItemViewModel(f)));
        NavItems[0].IsSelected = true;
        _selectedFeature = NavItems[0].Feature;
        Board.SetSection(_selectedFeature.Id, _selectedFeature.DisplayName);

        // Every list add / remove / clear is also the moment the on-disk record changes: the job
        // list is memory-only, so without this the answer to 「今天扫了哪些」 dies with the process.
        Board.Jobs.CollectionChanged += (_, _) =>
        {
            ScheduleJournalSave();
            OnPropertyChanged(nameof(CanUploadBatch));
        };

        // The job row owns scan progress (phase + page count). A second copy in the scan panel
        // and a third in the footer made one action read as three competing status lines.
        _scan = new ScanCoordinator(Settings, busy => IsBusy = busy);
        _naming = new ContentNamingService(Settings, SharedPdfRenderer, SharedOcrEngine);
        _renamer = new JobRenamer(_dialogs);
        _preview = new PreviewStripCoordinator(
            Board.Jobs, _dialogs, SharedPdfRenderer, SharedOcrEngine,
            scanIntoStrip: progress => _scan.ScanIntoStripAsync(progress));
        Netdisk = new NetdiskCoordinator(Settings, _dialogs, () => IsBusy);

        OpenOutputCommand = new RelayCommand(p => OpenOutput(p as string), p => p is string path && File.Exists(path));
        SaveSettingsCommand = new RelayCommand(() => SaveSettings());
        InstallUpdateCommand = new RelayCommand(OnUpdateIcon, () => UpdateReady.HasUpdate);
        UpdateReady.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateReadyNotifier.HasUpdate))
                InstallUpdateCommand.RaiseCanExecuteChanged();
        };
        ScanCommand = new RelayCommand(() => _ = RunScanAsync(), () => IsIdle && !IsScanning);
        CancelScanCommand = new RelayCommand(() =>
        {
            _scanLoopStopped = true;
            _scan.CancelScan();
        }, () => IsScanning);
        PreviewCommand = new RelayCommand(p => _preview.Toggle(p as FileJobViewModel), p => p is FileJobViewModel);
        UploadJobCommand = new RelayCommand(
            p => { if (p is FileJobViewModel job) _ = UploadJobAsync(job); },
            p => p is FileJobViewModel);
        UploadVisibleCommand = new RelayCommand(() => _ = UploadVisibleAsync(), () => CanUploadBatch);

        // --- 手机传文件 ---
        // The LAN receiver lives as long as the app; landed files become finished job rows in
        // the 手机传文件 section (rename / chips / delete / open-folder all work unchanged).
        Transfer = new TransferPanelViewModel(Settings, _shell, message => AppLog.Trace(message, null));
        Transfer.FileReceived += OnTransferFileReceived;
        Transfer.ShareDocumentReceived += OnTransferShareDocumentReceived;

        // --- 快速搜索 ---
        // The page asks the local index host over a named pipe. The index itself lives in a
        // privileged background process, so this app never needs elevation to search.
        Search = new SearchPanelViewModel(new IndexServiceSearchProvider(), _shell);

        // Last, once every service the restored rows talk to (netdisk accounts included) exists.
        RestoreJournal();
    }

    /// <summary>Logs the resolved engine (and every skipped link) once per process and returns
    /// the switchable proxy the features keep for their whole lifetime.</summary>
    private static IOcrEngine CreateOcrEngine()
    {
        foreach (ProbeLink<IOcrEngine> link in AppOcrEngineChain.Instance.Links)
        {
            if (link.Available)
                AppLog.Info($"OCR 引擎：{link.Name}");
            else
                AppLog.Info($"未启用 {link.Name}：{link.Reason}");
        }

        return AppOcrEngineChain.Current;
    }

    public AppSettings Settings { get; }

    /// <summary>Left-navigation entries, in registration (priority) order.</summary>
    public ObservableCollection<FeatureNavItemViewModel> NavItems { get; }

    /// <summary>The navigation item picked in the sidebar; drives <see cref="SelectedFeature"/>.</summary>
    public FeatureNavItemViewModel SelectedNavItem
    {
        get => NavItems.First(n => n.IsSelected);
        set
        {
            if (value is null || value.IsSelected) return;
            foreach (var item in NavItems) item.IsSelected = ReferenceEquals(item, value);
            _selectedFeature = value.Feature;
            OnPropertyChanged(nameof(SelectedFeature));
            OnSelectedFeatureChanged();
        }
    }

    /// <summary>The feature every newly added file is processed with.</summary>
    public IFeature SelectedFeature
    {
        get => _selectedFeature;
        set
        {
            if (SetProperty(ref _selectedFeature, value))
            {
                foreach (var item in NavItems) item.IsSelected = ReferenceEquals(item.Feature, value);
                OnSelectedFeatureChanged();
            }
        }
    }

    public string AppVersion =>
        "v" + (System.Reflection.Assembly.GetEntryAssembly()
            ?.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>The shared job store and its per-section view (counts, status, 清除/删除).</summary>
    public JobBoard Board { get; }

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    /// <summary>
    /// The shell's update state: a verified package downloaded in the background shows an icon
    /// until the user restarts into it. Bound straight to the notifier so the background thread
    /// that finishes the download can flip the icon without the view model in the middle.
    /// </summary>
    public UpdateReadyNotifier UpdateReady => UpdateReadyNotifier.Current;

    public RelayCommand InstallUpdateCommand { get; }

    /// <summary>
    /// The update icon does whatever its state needs: restart into a ready package, retry a
    /// failed download, or say that a background download is still running (so the click never
    /// looks dead, which is exactly what the reporting machine complained about).
    /// </summary>
    private void OnUpdateIcon()
    {
        switch (UpdateReady.State)
        {
            case UpdateVisualState.Failed:
                AppLog.Info($"用户点击更新图标重试 v{UpdateReady.Version}");
                _ = UpdateCoordinator.RetryAsync(Settings);
                break;

            case UpdateVisualState.Downloading:
                _dialogs.Info(
                    UpdateReady.Percent is { } percent
                        ? $"更新正在后台下载（{percent}%），完成后图标会提示重启。\n关闭窗口不会中断下载。"
                        : "更新正在后台下载，完成后图标会提示重启。\n关闭窗口不会中断下载。",
                    "软件更新");
                break;

            case UpdateVisualState.Ready:
                UpdateCoordinator.ApplyReady(_dialogs);
                break;
        }
    }

    public RelayCommand ScanCommand { get; }

    /// <summary>Cancels the running scan; pages already captured are saved as a partial PDF.</summary>
    public RelayCommand CancelScanCommand { get; }

    /// <summary>True only while the scanner session is running — gates the cancel button.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
                CancelScanCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Expands / folds the thumbnail strip under the finished record; the parameter is the row.</summary>
    public RelayCommand PreviewCommand { get; }

    /// <summary>Uploads the row passed as the parameter; the row is the unit of accounting.</summary>
    public RelayCommand UploadJobCommand { get; }

    /// <summary>Uploads every finished, not-yet-uploaded record of the current section.</summary>
    public RelayCommand UploadVisibleCommand { get; }

    /// <summary>Footer gate for 「上传本页全部」: an account exists and something is left to send.</summary>
    public bool CanUploadBatch => Netdisk.HasAccounts && Board.Jobs.Any(IsUploadCandidate);

    public string UploadBatchHint => Netdisk.HasAccounts
        ? $"把本页已完成的文件上传到「{Netdisk.PrimaryAccountName}」"
        : "先在「设置 → 网盘设置」中添加网盘账号";

    /// <summary>A row the batch upload may take: finished, not uploaded yet, file still on disk.</summary>
    private bool IsUploadCandidate(FileJobViewModel job) =>
        job.OwnerFeatureId == _selectedFeature.Id
        && job.State == JobState.Succeeded
        && job.Upload is not (UploadState.Uploaded or UploadState.Uploading)
        && job.OutputPath is not null
        && File.Exists(job.OutputPath);

    /// <summary>
    /// True while the settings second-level page covers the whole window. The view switches
    /// between the main shell and the settings page purely from this flag — no new window.
    /// </summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set
        {
            if (SetProperty(ref _isSettingsOpen, value))
            {
                OnPropertyChanged(nameof(IsMainPage));
                // The settings page may have just added the first netdisk account, which is what
                // turns the footer upload button on.
                OnPropertyChanged(nameof(CanUploadBatch));
                OnPropertyChanged(nameof(UploadBatchHint));
            }
        }
    }

    /// <summary>Convenience inverse so the main shell binds with a single bool.</summary>
    public bool IsMainPage => !IsSettingsOpen;

    /// <summary>
    /// Opens the screen-level image viewer for a strip (host-supplied, so the view model never
    /// touches windows). A tile click routes here: full-screen dark veil, the page in the middle,
    /// the film-rail directory on the left — the interaction every desktop image browser uses.
    /// </summary>
    public Action<PdfPreviewViewModel>? PreviewViewerOpener
    {
        get => _preview.ViewerOpener;
        set => _preview.ViewerOpener = value;
    }

    /// <summary>The 网盘 section's state (products, accounts, management window, quick upload).</summary>
    public NetdiskCoordinator Netdisk { get; }

    /// <summary>The settings page view model; non-null only while the page is open.</summary>
    public SettingsViewModel? SettingsPage
    {
        get => _settingsPage;
        private set => SetProperty(ref _settingsPage, value);
    }

    /// <summary>Enters the settings second-level page (fresh working copy each time).</summary>
    public void OpenSettings()
    {
        var page = new SettingsViewModel(Settings, _dialogs, _shell, netdisk: Netdisk.Settings);
        page.RefreshLog();
        SettingsPage = page;
        IsSettingsOpen = true;
        AppLog.Info("打开设置页");
    }

    /// <summary>
    /// Back to the main page: validates the output directory, commits the working copy and
    /// persists. Returns false when validation fails so the page stays open.
    /// </summary>
    public bool SaveAndExitSettings()
    {
        if (SettingsPage is not { } page) { IsSettingsOpen = false; return true; }

        if (page.Settings.OutputMode == OutputMode.CustomDirectory)
        {
            string directory = page.Settings.OutputDirectory.Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                _dialogs.Warn("请填写输出目录，或选择「与源文件相同目录」。", "提示");
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                _dialogs.Warn($"输出目录不可用：{ex.Message}", "提示");
                return false;
            }

            page.Settings.OutputDirectory = directory;
        }

        page.Commit();
        if (!SaveSettings())
        {
            // Closing now would look like a successful save while the changes die with the
            // process; keep the page open so the user can retry or discard.
            _dialogs.Warn(
                "设置保存失败（配置目录不可写），本次更改在退出后不会保留。请检查目录权限后重试。",
                "保存失败");
            return false;
        }
        CloseSettings();
        return true;
    }

    /// <summary>Back to the main page, dropping every edit made in the working copy.</summary>
    public void DiscardAndExitSettings()
    {
        CloseSettings();
        AppLog.Info("设置页更改已放弃");
    }

    private void CloseSettings()
    {
        IsSettingsOpen = false;
        SettingsPage = null;
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                Board.IsBusy = value;
                ScanCommand.RaiseCanExecuteChanged();
                Netdisk.RefreshCommandState();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>Section switch: swap the board's view filter without rebuilding the data.</summary>
    private void OnSelectedFeatureChanged()
    {
        Board.SetSection(_selectedFeature.Id, _selectedFeature.DisplayName);
        OnPropertyChanged(nameof(DropHint));
        OnPropertyChanged(nameof(FilePickerFilter));
        OnPropertyChanged(nameof(FilePickerTitle));
        // Each section owns its own upload batch: the footer button follows the page.
        OnPropertyChanged(nameof(CanUploadBatch));
        OnPropertyChanged(nameof(UploadBatchHint));
    }

    private DropZoneState _dropZoneState = DropZoneState.Idle;
    private string? _rejectHint;

    /// <summary>Current drop-zone state; the XAML binds every visual aspect (outline, tint,
    /// icon, text color) to this, so drag handlers never touch controls directly.</summary>
    public DropZoneState DropZoneState
    {
        get => _dropZoneState;
        private set
        {
            if (SetProperty(ref _dropZoneState, value))
            {
                OnPropertyChanged(nameof(DropHint));
                OnPropertyChanged(nameof(DropZoneIcon));
            }
        }
    }

    /// <summary>Upload glyph for idle/accept; switches to the cancel glyph when rejecting.</summary>
    public string DropZoneIcon => DropZoneState == DropZoneState.Reject ? "\uE711" : "\uE74C";

    /// <summary>State-aware hint; always derives from the selected feature so newly registered
    /// features get the right prompt without touching the view.</summary>
    public string DropHint => DropZoneState switch
    {
        DropZoneState.Accept => $"松开鼠标，立即开始「{SelectedFeature.DisplayName}」",
        DropZoneState.Reject => _rejectHint ?? $"「{SelectedFeature.DisplayName}」不支持这些文件",
        _ => $"把文件拖到这里，松手即开始「{SelectedFeature.DisplayName}」",
    };

    /// <summary>Whether a dropped path is usable: a file the feature handles, or a folder that
    /// may contain some (expanded one level deep on drop).</summary>
    public bool CanAcceptPath(string path) =>
        Directory.Exists(path) || SelectedFeature.CanHandle(path);

    /// <summary>Open-file dialog filter built from what the current feature accepts, so the
    /// picker never offers a format the page would reject (OCR takes photos, watermark does not).</summary>
    public string FilePickerFilter
    {
        get
        {
            IReadOnlyList<string> extensions = SelectedFeature.InputExtensions;
            if (extensions.Count == 0) return "PDF 文件 (*.pdf)|*.pdf";

            string patterns = string.Join(";", extensions.Select(extension => "*" + extension));
            string label = extensions.Count > 1 ? "支持的文件" : "PDF 文件";
            return $"{label} ({patterns})|{patterns}";
        }
    }

    /// <summary>Dialog title matching the filter width.</summary>
    public string FilePickerTitle =>
        SelectedFeature.InputExtensions.Count > 1 ? "选择文件" : "选择 PDF 文件";

    /// <summary>Called by the view on every drag heartbeat. Null = the payload carries no files
    /// at all; otherwise accept when any file matches the selected feature's CanHandle.</summary>
    public void NotifyDragOver(string[]? files)
    {
        if (IsBusy)
        {
            _rejectHint = "正在处理文件，完成后再添加";
            DropZoneState = DropZoneState.Reject;
        }
        else if (files is null)
        {
            _rejectHint = "这里只能拖入文件或文件夹";
            DropZoneState = DropZoneState.Reject;
        }
        else if (files.Any(CanAcceptPath))
        {
            _rejectHint = null;
            DropZoneState = DropZoneState.Accept;
        }
        else
        {
            _rejectHint = $"「{SelectedFeature.DisplayName}」不支持这些文件";
            DropZoneState = DropZoneState.Reject;
        }
    }

    /// <summary>Back to idle when the drag leaves the window or finishes.</summary>
    public void NotifyDragLeave()
    {
        _rejectHint = null;
        DropZoneState = DropZoneState.Idle;
    }

    /// <summary>A folder drop expands one level deep; this ceiling keeps a whole drive from being
    /// swept into the queue by accident.</summary>
    private const int MaxDropFiles = 500;

    /// <summary>Entry point for drag-and-drop and the file picker.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy)
        {
            // Silently swallowing the drop read as "the app is broken"; say why instead.
            _dialogs.Warn("正在处理文件，请等待当前任务完成后再添加。", "请稍候");
            return;
        }

        string[] input = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        (string[] files, int ignored) = ExpandDrop(input);

        if (files.Length == 0)
        {
            // Only worth a prompt when the user dropped something that needed expanding —
            // an unsupported file was already rejected by the drop zone itself.
            if (input.Any(Directory.Exists))
                _dialogs.Info("这个文件夹里没有可处理的文件（只读取第一层）。", "提示");
            else if (input.Any(File.Exists))
                _dialogs.Info($"「{SelectedFeature.DisplayName}」不支持所拖入的文件。", "提示");
            return;
        }

        // A mixed drop used to swallow the rejects without a word; one line accounts for them.
        if (ignored > 0)
            _dialogs.Info(
                $"已忽略 {ignored} 个不支持「{SelectedFeature.DisplayName}」的文件，" +
                $"其余 {files.Length} 个已加入列表。",
                "提示");

        AppLog.Info($"收到 {files.Length} 个待处理文件");
        string previewNames = string.Join("、", files.Take(20).Select(Path.GetFileName));
        AppLog.Debug(files.Length > 20
            ? $"待处理文件：{previewNames}…等 {files.Length} 个"
            : $"待处理文件：{previewNames}");

        var newJobs = files.Select(f =>
        {
            var job = new FileJobViewModel(f, Settings.QuickSuffixes)
            {
                OwnerFeatureId = SelectedFeature.Id,
            };
            WireJob(job);
            return job;
        }).ToList();
        foreach (var job in newJobs) Board.Add(job);
        Board.ResetCompleted();

        await ProcessJobsAsync(newJobs, SelectedFeature).ConfigureAwait(true);
    }

    /// <summary>Expands dropped folders one level and keeps only what the selected feature takes.
    /// Mixed drops are normal, so the count of explicitly dropped files that were rejected comes
    /// back with the result — the caller can say how many were skipped instead of dropping them
    /// silently. Files filtered out of a whole folder are ordinary and not counted.</summary>
    private (string[] Files, int Ignored) ExpandDrop(IEnumerable<string> paths)
    {
        var files = new List<string>();
        int ignored = 0;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(path)
                        .Where(SelectedFeature.CanHandle)
                        .Take(MaxDropFiles));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"展开文件夹失败：{path}（{ex.Message}）");
                }
            }
            else if (File.Exists(path))
            {
                if (SelectedFeature.CanHandle(path)) files.Add(path);
                else ignored++;
            }
        }

        return (files.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxDropFiles).ToArray(), ignored);
    }

    private async Task ProcessJobsAsync(IReadOnlyList<FileJobViewModel> jobs, IFeature feature)
    {
        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Snapshot once per batch: background threads read this copy, the settings window
            // keeps editing the live object — the two can never interfere.
            var context = FeatureContext.Capture(Settings, (inputPath, stage) =>
            {
                // 工作线程回调：把阶段文案路由到属主行，行内再 marshal 回 UI 线程。
                jobs.FirstOrDefault(j => string.Equals(j.InputPath, inputPath, StringComparison.OrdinalIgnoreCase))
                    ?.ReportStage(stage);
            });
            int maxConcurrency = Math.Clamp(Settings.MaxConcurrency, 1, 8);
            AppLog.Info($"批次处理开始：{jobs.Count} 个文件，并发 {maxConcurrency}");

            await _queue.RunAsync(jobs, feature, context, maxConcurrency, job =>
            {
                Board.MarkCompleted(job);
                _preview.OpenWhenFinished(job);
            }).ConfigureAwait(true);

            AppLog.Info($"批次处理结束：{jobs.Count} 个文件，并发 {maxConcurrency}，" +
                        $"总耗时 {stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gap between documents in continuous mode — long enough to swap the sheet stack.</summary>
    private static readonly TimeSpan ContinuousScanPause = TimeSpan.FromMilliseconds(1200);

    /// <summary>Set by 「取消扫描」 and by quitting: stops the chain even while no session runs.</summary>
    private bool _scanLoopStopped;

    /// <summary>
    /// Runs one scan (or, in continuous mode, a chain of them) with the layout chosen on the scan
    /// page. The row exists from the start and updates per page (扫描中 · 已读 N 页); once the PDF
    /// lands the same row is adopted as the completed record, so rename / suffix chips / delete
    /// all keep working unchanged.
    /// </summary>
    private async Task RunScanAsync()
    {
        // IsScanning covers the whole chain including its pause, so it is the re-entry guard:
        // without it a second click could start a parallel loop on top of the productive one.
        if (IsBusy || IsScanning) return;

        _scanLoopStopped = false;
        // One IsScanning span covers the whole chain, not just a single pass: 「取消扫描」 then owns
        // the pause between documents too, and nothing else can grab the scanner in the gap.
        IsScanning = true;
        try
        {
            while (true)
            {
                if (!await RunScanPassAsync().ConfigureAwait(true)) return;
                if (!ContinuousScan || _scanLoopStopped) return;

                AppLog.Info("连续扫描：等待下一份原稿");
                await Task.Delay(ContinuousScanPause).ConfigureAwait(true);
                if (_scanLoopStopped) return;
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>One scanner session; true means "the document landed, another may follow".</summary>
    private async Task<bool> RunScanPassAsync()
    {
        using var op = AppLog.BeginOperation(LogCategory.Scan, "扫描流程", slowMs: 0);

        FileJobViewModel job = AddScanningJob();
        bool scanFinished = false;
        Task<AutoNameResult?>? earlyNaming = null;
        // Every scan message (connecting / opening session / reading page N) lands on the row
        // itself; that is the only status surface for a scan.
        var stageProgress = new Progress<string>(job.ReportStage);
        var pageProgress = new Progress<ScannedPageInfo>(page =>
        {
            job.AddLiveThumbnail(page.ThumbnailPng);
            AppLog.Debug($"扫描页到达：第 {page.PageNumber} 页，" +
                         $"{page.Width}×{page.Height}，缩略图 {page.ThumbnailPng.Length / 1024} KB");

            // Page 1 carries a naming-resolution image: OCR it now, while the scanner keeps
            // feeding the remaining pages, instead of after the finished PDF.
            if (!scanFinished && earlyNaming is null && page.NamingImage is { Length: > 0 } namingImage)
            {
                AppLog.Debug($"扫描页到达：第 {page.PageNumber} 页附带命名图，" +
                             $"{namingImage.Length / 1024} KB，开始首屏命名");
                earlyNaming = Task.Run(() => _naming.TrySuggestFromImageAsync(namingImage));
            }
        });

        ScanOutcome outcome;
        try
        {
            outcome = await _scan.ScanOnceAsync(stageProgress, pageProgress).ConfigureAwait(true);
        }
        catch (ScannerBusyException ex)
        {
            Board.Remove(job);
            op.Complete($"未开始：{ex.Message}");
            _dialogs.Info(ex.Message, "提示");
            return false;
        }
        finally
        {
            scanFinished = true;
        }

        op.Step("扫描设备", outcome.Success
            ? $"{outcome.PageCount} 页，来源 {outcome.SourceName}"
            : outcome.Error ?? "扫描失败");

        if (!outcome.Success)
        {
            if (outcome.Cancelled && outcome.PageCount == 0)
            {
                AppLog.Info("扫描已取消（未读取到页面）");
                Board.Remove(job);
                op.Complete("已取消，未读取到页面");
                return false;
            }

            if (ContinuousScan && outcome.PageCount == 0 && !outcome.Cancelled)
            {
                // An empty feeder is how a continuous batch normally ends — not a red failure.
                AppLog.Info("连续扫描结束：未读取到页面");
                Board.Remove(job);
                op.Complete("连续扫描结束：未读取到页面");
                _dialogs.Info("连续扫描结束：没有读取到页面。", "连续扫描");
                return false;
            }

            AppLog.Error($"扫描失败：{outcome.Error}");
            job.Apply(FeatureResult.FailWithDetails(
                job.InputPath, outcome.Error ?? "扫描失败", outcome.ErrorDetails));
            job.ClearLiveThumbnails();
            op.Fail(outcome.Error ?? "扫描失败");
            _dialogs.Error(outcome.Error ?? "扫描失败", "扫描失败");
            return false;
        }

        // The PDF is both input and output of the record; adopting the real path keeps the
        // row's file name (delete prompt, diagnostics) consistent with what landed on disk.
        string path = outcome.OutputPath!;
        job.AdoptSource(path);
        string summary = outcome.Cancelled
            ? $"已取消，保留 {outcome.PageCount} 页"
            : $"扫描 {outcome.PageCount} 页";
        job.Apply(FeatureResult.Success(path, path, summary, outcome.PageCount));
        // Hand the pages already on screen to the strip so it opens with real thumbnails instead
        // of an empty grid: the live row then disappears without the strip visibly rebuilding.
        var livePages = job.LiveThumbnails.ToArray();
        Board.MoveToTop(job);
        op.Step("接收落盘", $"{Path.GetFileName(path)}，{outcome.PageCount} 页");

        // The document exists but not where it was meant to: say so now, while the page count is
        // still in front of the user, instead of leaving a file silently stranded in %TEMP%.
        if (outcome.Warning is { Length: > 0 } warning)
        {
            AppLog.Warn($"扫描保存异常：{warning}");
            _dialogs.Warn(warning, "扫描保存");
        }

        // Zero-click preview: the strip unfolds the moment the scan lands.
        _preview.Open(job, livePages);
        job.ClearLiveThumbnails();
        op.Step("打开预览", Path.GetFileName(path));

        // Naming may already have finished from page 1 while the scan continued; the rename
        // still waits for the PDF (and the strip's writes) before moving the file.
        _ = RenameScanInBackgroundAsync(job, path, earlyNaming);
        op.Step("启动后台命名", earlyNaming is null ? "无首屏命名结果" : "复用首屏命名结果");

        op.Complete(summary);
        // 「取消扫描」 with pages kept means the user is done: never roll into another document.
        return !outcome.Cancelled;
    }

    /// <summary>A row that exists for the whole scan: live page count first, the finished PDF
    /// record once <see cref="FileJobViewModel.AdoptSource"/> hands it the real path.</summary>
    private FileJobViewModel AddScanningJob()
    {
        var job = new FileJobViewModel("扫描中", Settings.QuickSuffixes)
        {
            OwnerFeatureId = "scan.page",
        };
        WireJob(job);
        job.MarkProcessing("正在准备扫描…");
        Board.ResetCompleted();
        Board.Add(job);
        Board.MoveToTop(job);
        return job;
    }

    /// <summary>
    /// Fire-and-forget content naming for a fresh scan: the row is already visible under its
    /// timestamp name; when the chain lands a confident name the file moves and the row follows
    /// (the open strip re-finds its file by fingerprint when it next needs the path). Naming
    /// must never fail the scan — every exception lands in the log, the timestamp name stays.
    /// </summary>
    private async Task RenameScanInBackgroundAsync(
        FileJobViewModel job,
        string pdfPath,
        Task<AutoNameResult?>? earlyNaming = null)
    {
        using var op = AppLog.BeginOperation(
            LogCategory.Naming, "后台命名", Path.GetFileName(pdfPath));
        // The row says "完成" the moment the PDF lands, so naming needs its own line or the
        // work (and its failure) is completely invisible.
        bool namingOn = Settings.AutoNameByContent;
        if (namingOn) job.SetNamingNote("识别命名中…");
        try
        {
            // The open strip auto-orients / rewrites the file right after landing; a rename
            // (File.Move) during that write throws, so drain the strip's busy passes first.
            await (job.Strip?.WaitForSettledAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            op.Step("等待预览稳定");

            // Use the page-1 suggestion when it produced one. Otherwise fall back to probing the
            // PDF — but not when page 1 was already probed and the settings pin naming to it.
            AutoNameResult? suggestion = earlyNaming is null
                ? null
                : await earlyNaming.ConfigureAwait(false);
            string finalPath;
            if (suggestion is not null)
            {
                finalPath = await _naming.ApplyAsync(pdfPath, suggestion).ConfigureAwait(false);
                op.Step("复用首屏命名", Path.GetFileName(finalPath));
            }
            else if (earlyNaming is not null && Settings.NameFromFirstPageOnly)
            {
                finalPath = pdfPath;
                op.Step("PDF 探测", "首屏已探测且设置仅用首页，跳过整页探测");
            }
            else
            {
                finalPath = await _naming.TryRenameAsync(pdfPath).ConfigureAwait(false);
                op.Step("PDF 探测", Path.GetFileName(finalPath));
            }

            if (string.Equals(finalPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                if (namingOn) job.SetNamingNote("未识别到标题，保留时间戳名");
                op.Complete("无更名");
                return;
            }

            AppLog.Info($"后台命名完成：{Path.GetFileName(pdfPath)} → {Path.GetFileName(finalPath)}");
            op.Step("落盘", Path.GetFileName(finalPath));

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                job.SetNamingNote(null);
                op.Complete($"→ {Path.GetFileName(finalPath)}");
                return;
            }

            dispatcher.Invoke(() =>
            {
                job.UpdateRenamed(finalPath);
                job.SetNamingNote(null);
            });
            op.Complete($"→ {Path.GetFileName(finalPath)}");
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：后台自动命名异常，保留时间戳命名", ex);
            if (namingOn) job.SetNamingNote("命名失败，保留时间戳名");
            op.Fail("后台自动命名异常，保留时间戳命名", ex);
        }
    }

    /// <summary>Persists the live settings; false when the file could not be written.</summary>
    private bool SaveSettings()
    {
        // The pipeline preference may have changed: repoint the proxy before persisting so the
        // next job already uses the newly chosen layout block and engine.
        AppOcrEngineChain.ApplyPipeline(Settings.PreferredLayoutStage, Settings.PreferredOcrEngine);

        if (SettingsService.Save(Settings))
        {
            AppLog.Info("设置已保存");
            // Quick suffixes may have changed: refresh the chips of every existing job.
            Board.RefreshSuffixChips(Settings.QuickSuffixes);
            return true;
        }

        AppLog.Error("设置保存失败（目录不可写？）");
        return false;
    }

    // --- 任务记录（对账） ----------------------------------------------

    /// <summary>Set while the journal is being read back so the row inserts of the restore pass
    /// do not each trigger a write of the same partial snapshot.</summary>
    private bool _restoringJournal;

    /// <summary>Connects a fresh row to the host: rename / chips are actions, and the three
    /// journal-relevant properties keep the on-disk record in step with the list.</summary>
    private void WireJob(FileJobViewModel job)
    {
        job.RenameRequested += OnJobRenameRequested;
        job.SuffixToggleRequested += OnJobSuffixToggleRequested;
        job.UploadRequested += OnJobUploadRequested;
        job.RetryRequested += OnJobRetryRequested;
        job.PropertyChanged += OnJobPropertyChanged;
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileJobViewModel.State)
            or nameof(FileJobViewModel.OutputPath)
            or nameof(FileJobViewModel.Upload))
        {
            ScheduleJournalSave();
            // A row just finished (or was uploaded): the footer batch gate changes with it.
            OnPropertyChanged(nameof(CanUploadBatch));
        }
    }

    /// <summary>
    /// Reads the journal back into the board: after a restart the list still answers 「今天扫了哪些、
    /// 传没传」. Rows whose file is gone are dropped — a record that cannot be opened, renamed or
    /// uploaded only misleads.
    /// </summary>
    private void RestoreJournal()
    {
        if (_journal is null) return;

        var knownSections = _registry.Features.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var restored = new List<FileJobViewModel>();

        _restoringJournal = true;
        try
        {
            foreach (var entry in _journal.Load().OrderBy(e => e.ProcessedAt))
            {
                if (!knownSections.Contains(entry.SectionId)) continue;
                if (string.IsNullOrWhiteSpace(entry.OutputPath) || !File.Exists(entry.OutputPath))
                    continue;

                var job = new FileJobViewModel(entry.InputPath, Settings.QuickSuffixes)
                {
                    OwnerFeatureId = entry.SectionId,
                };
                WireJob(job);

                job.Restore(
                    entry.InputPath, entry.OutputPath, entry.Summary, entry.ItemCount, entry.ProcessedAt);
                job.SetUploadState(
                    Enum.TryParse<UploadState>(entry.UploadState, out var upload) ? upload : UploadState.None,
                    entry.UploadNote);
                restored.Add(job);
            }

            Board.Restore(restored);
        }
        finally
        {
            _restoringJournal = false;
        }

        if (restored.Count > 0)
            AppLog.Info($"任务记录：恢复 {restored.Count} 条已完成记录");
        OnPropertyChanged(nameof(CanUploadBatch));
    }

    /// <summary>Writes the journal after the current change; cheap enough to run per record.</summary>
    private void ScheduleJournalSave()
    {
        if (_journal is null || _restoringJournal) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SaveJournal();
            return;
        }

        // Worker threads finish in bursts: queue one save and let it snapshot the latest list.
        if (Interlocked.Exchange(ref _journalSaveQueued, 1) == 1) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _journalSaveQueued, 0);
            SaveJournal();
        });
    }

    /// <summary>Snapshots every finished row (upload state included) — UI thread only.</summary>
    private void SaveJournal()
    {
        if (_journal is null) return;

        var entries = Board.Jobs
            .Where(j => j.State == JobState.Succeeded && !string.IsNullOrEmpty(j.OutputPath))
            .Select(j => j.ToJournalEntry())
            .ToList();

        if (!_journal.Save(entries)) return;
        AppLog.Trace($"任务记录：写入 {entries.Count} 条", null);
    }

    // --- 上传到网盘 ----------------------------------------------------

    private void OnJobUploadRequested(object? sender, EventArgs e)
    {
        if (sender is FileJobViewModel job) _ = UploadJobAsync(job);
    }

    /// <summary>One row → netdisk. Every outcome lands as a row state + note; the click never
    /// dies silently, even with no account configured.</summary>
    private async Task UploadJobAsync(FileJobViewModel job)
    {
        if (job.OutputPath is null || !File.Exists(job.OutputPath))
        {
            job.SetUploadState(UploadState.Failed, "文件已不存在，无法上传");
            return;
        }

        if (!Netdisk.HasAccounts)
        {
            _dialogs.Warn("还没有配置网盘账号。请先在「设置 → 网盘设置」中添加一个账号。", "上传到网盘");
            return;
        }

        job.SetUploadState(UploadState.Uploading);
        var failures = await Netdisk.UploadFilesAsync(new[] { job.OutputPath });
        ApplyUploadResult(new[] { job }, failures);
    }

    /// <summary>Footer 「上传本页全部」: one connection for every candidate row of the section.</summary>
    private async Task UploadVisibleAsync()
    {
        if (!Netdisk.HasAccounts)
        {
            _dialogs.Warn("还没有配置网盘账号。请先在「设置 → 网盘设置」中添加一个账号。", "上传到网盘");
            return;
        }

        var targets = Board.Jobs.Where(IsUploadCandidate).ToList();
        if (targets.Count == 0) return;

        foreach (var job in targets) job.SetUploadState(UploadState.Uploading);

        IReadOnlyList<(string Path, string Error)> failures;
        try
        {
            failures = await Netdisk.UploadFilesAsync(targets.Select(j => j.OutputPath!).ToList());
        }
        catch (Exception ex)
        {
            // The coordinator reports per-file failures; a throw here is unexpected, and a row
            // stuck in 「上传中…」 would never recover, so fail them all explicitly.
            AppLog.Error("批量上传异常", ex);
            failures = targets.Select(j => (j.OutputPath!, ex.Message)).ToList();
        }

        ApplyUploadResult(targets, failures);

        if (failures.Count == 0)
            _dialogs.Info($"已上传 {targets.Count} 个文件到「{Netdisk.PrimaryAccountName}」。", "上传到网盘");
        else
            _dialogs.Warn(
                $"{failures.Count} 个文件上传失败（第一个：{failures[0].Error}）。\n\n" +
                "失败的文件保留了「上传失败」标记，可单独点击重试。", "上传到网盘");
    }

    private void ApplyUploadResult(
        IReadOnlyList<FileJobViewModel> jobs, IReadOnlyList<(string Path, string Error)> failures)
    {
        var failed = failures.ToDictionary(f => f.Path, f => f.Error, StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (job.OutputPath is not null && failed.TryGetValue(job.OutputPath, out string? error))
            {
                job.SetUploadState(UploadState.Failed, error);
                AppLog.Warn($"网盘上传失败：{job.OutputPath}（{error}）");
            }
            else
            {
                job.SetUploadState(UploadState.Uploaded, $"已上传到「{Netdisk.PrimaryAccountName}」");
                AppLog.Info($"网盘上传成功：{job.OutputPath}");
            }
        }

        ScheduleJournalSave();
        OnPropertyChanged(nameof(CanUploadBatch));
    }

    /// <summary>Persists the scan layout choice immediately — the scan page has no save button.
    /// A no-op when the value already matches, so the initial radio sync never writes.</summary>
    public void ApplyScanDuplex(int duplex)
    {
        if (Settings.ScanDuplex == duplex) return;

        Settings.ScanDuplex = duplex;
        if (SettingsService.Save(Settings))
            AppLog.Info($"扫描版面已保存：{duplex}");
        else
            AppLog.Error("扫描版面保存失败（目录不可写？）");

        OnPropertyChanged(nameof(IsScanSimplex));
        OnPropertyChanged(nameof(IsScanDuplexLongEdge));
        OnPropertyChanged(nameof(IsScanDuplexShortEdge));
    }

    // Radio bindings for the scan strip (rendered from Page.Scan): selecting persists at once.
    public bool IsScanSimplex
    {
        get => Settings.ScanDuplex == 0;
        set { if (value) ApplyScanDuplex(0); }
    }

    public bool IsScanDuplexLongEdge
    {
        get => Settings.ScanDuplex == 1;
        set { if (value) ApplyScanDuplex(1); }
    }

    public bool IsScanDuplexShortEdge
    {
        get => Settings.ScanDuplex == 2;
        set { if (value) ApplyScanDuplex(2); }
    }

    /// <summary>连续扫描: chain documents without touching the button again. Edited in 设置 →
    /// 扫描 and persisted with the rest of that page; the scan loop always reads the live value.</summary>
    public bool ContinuousScan => Settings.ContinuousScan;

    private void OnJobRenameRequested(object? sender, string desiredName)
    {
        if (sender is FileJobViewModel job) _renamer.Rename(job, desiredName);
    }

    private void OnJobSuffixToggleRequested(object? sender, string suffix)
    {
        if (sender is FileJobViewModel job) _renamer.ApplySuffix(job, suffix);
    }

    /// <summary>A failed scan row's 「重新扫描」: drop the dead row and start a fresh session with
    /// the same settings — the file never existed, so there is nothing to reprocess.</summary>
    private void OnJobRetryRequested(object? sender, EventArgs e)
    {
        if (sender is not FileJobViewModel job || !IsIdle || IsScanning) return;

        AppLog.Info("扫描失败记录：重新扫描");
        Board.Remove(job);
        _ = RunScanAsync();
    }

    /// <summary>
    /// Window close guard: a scan or a batch still running means unsaved work. Returns false when
    /// the user chooses to stay; on accept, the scanner session is cancelled so the device is not
    /// left running behind a dying process.
    /// </summary>
    public bool ConfirmExit()
    {
        if (!IsBusy && !IsScanning) return true;

        string what = IsScanning ? "正在扫描" : "正在处理文件";
        bool leave = _dialogs.Confirm(
            $"{what}，现在退出会丢失尚未完成的内容。\n\n确定要退出吗？", "退出确认");
        if (!leave) return false;

        AppLog.Info("退出应用：用户确认中断进行中的任务");
        _scanLoopStopped = true;
        _scan.CancelScan();
        return true;
    }

    /// <summary>Builds and unfolds the row's strip immediately (scans call this the moment the
    /// PDF lands). Kept as a test/host entry point; the strip lifecycle lives in
    /// <see cref="PreviewStripCoordinator"/>.</summary>
    internal void OpenPreviewStrip(FileJobViewModel job) => _preview.Open(job);

    private void OpenOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        // Reveal the file in Explorer rather than opening the PDF viewer.
        string? directory = Path.GetDirectoryName(path);
        if (directory is null) return;

        if (!_shell.TryOpenDirectory(directory, out string? error))
            _dialogs.Info($"无法打开输出目录：{error}", "提示");
    }

    // --- 手机传文件 ---------------------------------------------------

    /// <summary>The 手机传文件 page's state (start/stop, QR, receive tally); wired to the shell.</summary>
    public TransferPanelViewModel Transfer { get; }

    /// <summary>快速搜索 page state (query, hits, open/reveal/copy actions).</summary>
    public SearchPanelViewModel Search { get; }

    /// <summary>Socket thread → UI thread: each landed phone file becomes a finished record in
    /// the 手机传文件 section, so rename, suffix chips, delete, upload and open-folder all work
    /// unchanged.</summary>
    private void OnTransferFileReceived(string path, long bytes)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) _ = AddReceivedFileAsync(path, bytes);
        else dispatcher.Invoke(() => _ = AddReceivedFileAsync(path, bytes));
    }

    /// <summary>
    /// Received files get the same content naming as scans before their row appears: a phone photo
    /// of a document lands under its recognised name, not 「IMG_2031.jpg」. Naming never blocks the
    /// record — any failure (or an unsupported type) keeps the name it arrived with.
    /// </summary>
    private async Task AddReceivedFileAsync(string path, long? receivedBytes)
    {
        string finalPath = path;
        try
        {
            finalPath = await _naming.TryRenameReceivedAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Trace("手机传文件：自动命名异常，保留原文件名", ex);
        }

        long bytes = receivedBytes ?? 0;
        try { bytes = new FileInfo(finalPath).Length; } catch { /* best effort — size display only */ }
        AddTransferJob(finalPath, bytes);
    }

    private void AddTransferJob(string path, long bytes)
    {
        string size = bytes switch
        {
            >= 1L << 30 => $"{bytes / 1073741824.0:F2} GB",
            >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
                _ => $"{Math.Max(1, bytes / 1024)} KB",
        };
        var job = new FileJobViewModel(path, Settings.QuickSuffixes)
        {
            OwnerFeatureId = "transfer.page",
        };
        WireJob(job);
        job.Apply(FeatureResult.Success(path, path, $"来自手机 · {size}", 1));
        Board.Add(job);
        Board.MoveToTop(job);
    }

    /// <summary>Worker thread → UI thread: the PDF assembled from a CamScanner share link joins
    /// the list as a finished record after the same content-based naming scans get.</summary>
    private void OnTransferShareDocumentReceived(string path)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.InvokeAsync(() => AddReceivedFileAsync(path, null));
    }

    /// <summary>Stops the LAN receiver; the window calls this on close so the port frees at once.</summary>
    public void Shutdown() => Transfer.Stop();
}
