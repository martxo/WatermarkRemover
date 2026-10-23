using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Core.Features.Transfer;
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
        new OcrFeature(SharedPdfRenderer, SharedOcrEngine, trace: AppLog.Trace),
        new NetdiskFeature(),
        new LanTransferFeature(),
    });

    private IFeature _selectedFeature;
    private readonly IDialogService _dialogs;
    private readonly IShellOpenService _shell;
    private readonly JobQueueService _queue = new();
    private readonly ScanCoordinator _scan;
    private readonly ContentNamingService _naming;
    private readonly JobRenamer _renamer;
    private readonly PreviewStripCoordinator _preview;
    private bool _isBusy;
    private bool _isSettingsOpen;
    private SettingsViewModel? _settingsPage;

    public MainViewModel()
        : this(DialogService.Default, ShellOpenService.Default)
    {
    }

    /// <summary>Service-injected constructor; tests drive the UI surface through fakes.</summary>
    internal MainViewModel(IDialogService dialogs, IShellOpenService shell)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        Settings = SettingsService.Load();
        // Honour the pipeline pinned in settings (empty = automatic priority chains).
        AppOcrEngineChain.ApplyPipeline(Settings.PreferredLayoutStage, Settings.PreferredOcrEngine);
        Board = new JobBoard(_dialogs);
        NavItems = new ObservableCollection<FeatureNavItemViewModel>(
            _registry.Features.Select(f => new FeatureNavItemViewModel(f)));
        NavItems[0].IsSelected = true;
        _selectedFeature = NavItems[0].Feature;
        Board.SetSection(_selectedFeature.Id, _selectedFeature.DisplayName);

        _scan = new ScanCoordinator(Settings, busy => IsBusy = busy, status => ScanStatus = status);
        _naming = new ContentNamingService(Settings, SharedPdfRenderer, SharedOcrEngine);
        _renamer = new JobRenamer(_dialogs);
        _preview = new PreviewStripCoordinator(
            Board.Jobs, _dialogs, SharedPdfRenderer, SharedOcrEngine,
            scanIntoStrip: progress => _scan.ScanIntoStripAsync(progress));
        Netdisk = new NetdiskCoordinator(Settings, _dialogs, () => IsBusy);

        OpenOutputCommand = new RelayCommand(p => OpenOutput(p as string), p => p is string path && File.Exists(path));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ScanCommand = new RelayCommand(() => _ = RunScanAsync(), () => IsIdle);
        PreviewCommand = new RelayCommand(p => _preview.Toggle(p as FileJobViewModel), p => p is FileJobViewModel);

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

    public RelayCommand ScanCommand { get; }

    /// <summary>Expands / folds the thumbnail strip under the finished record; the parameter is the row.</summary>
    public RelayCommand PreviewCommand { get; }

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
                OnPropertyChanged(nameof(IsMainPage));
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
        SaveSettingsCommand.Execute(null);
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

    private string _scanStatus = string.Empty;

    /// <summary>Live stage text from the scan flow, shown on the scan panel; empty at rest.</summary>
    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    /// <summary>Section switch: swap the board's view filter without rebuilding the data.</summary>
    private void OnSelectedFeatureChanged()
    {
        Board.SetSection(_selectedFeature.Id, _selectedFeature.DisplayName);
        OnPropertyChanged(nameof(DropHint));
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

    /// <summary>Called by the view on every drag heartbeat. Null = the payload carries no files
    /// at all; otherwise accept when any file matches the selected feature's CanHandle.</summary>
    public void NotifyDragOver(string[]? files)
    {
        if (files is null)
        {
            _rejectHint = "这里只能拖入文件";
            DropZoneState = DropZoneState.Reject;
        }
        else if (files.Any(SelectedFeature.CanHandle))
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

    /// <summary>Entry point for drag-and-drop and the file picker.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        string[] files = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

        if (files.Length == 0) return;

        AppLog.Info($"收到 {files.Length} 个待处理文件");

        var newJobs = files.Select(f =>
        {
            var job = new FileJobViewModel(f, Settings.QuickSuffixes)
            {
                OwnerFeatureId = SelectedFeature.Id,
            };
            job.RenameRequested += OnJobRenameRequested;
            job.SuffixToggleRequested += OnJobSuffixToggleRequested;
            return job;
        }).ToList();
        foreach (var job in newJobs) Board.Add(job);
        Board.ResetCompleted();

        await ProcessJobsAsync(newJobs, SelectedFeature).ConfigureAwait(true);
    }

    private async Task ProcessJobsAsync(IReadOnlyList<FileJobViewModel> jobs, IFeature feature)
    {
        IsBusy = true;
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

            await _queue.RunAsync(jobs, feature, context, maxConcurrency, job =>
            {
                Board.MarkCompleted(job);
                _preview.OpenWhenFinished(job);
            }).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs one scan with the layout chosen on the scan page, then files the produced PDF into
    /// the job list as a completed record so rename / suffix chips / delete all work unchanged.
    /// </summary>
    private async Task RunScanAsync()
    {
        if (IsBusy) return;

        ScanOutcome outcome;
        try
        {
            outcome = await _scan.ScanOnceAsync(progressSink: null).ConfigureAwait(true);
        }
        catch (ScannerBusyException ex)
        {
            _dialogs.Info(ex.Message, "提示");
            return;
        }

        if (!outcome.Success)
        {
            AppLog.Error($"扫描失败：{outcome.Error}");
            _dialogs.Error(outcome.Error ?? "扫描失败", "扫描失败");
            return;
        }

        // The row (and its unfolded thumbnail strip) lands the moment the scan saves —
        // naming/OCR must never hold the result hostage. The rename runs behind it; the
        // open strip tracks the rename of its own file by fingerprint when it next
        // needs the path.
        FileJobViewModel job = AddScanJob(outcome.OutputPath!, outcome.PageCount);
        _ = RenameScanInBackgroundAsync(job, outcome.OutputPath!);
    }

    /// <summary>Files a finished scan into the job list. The PDF is both input and output of the
    /// record, so every existing interaction (rename, chips, delete) applies without changes.
    /// Returns the row so the (async) content-naming chain can follow it.</summary>
    private FileJobViewModel AddScanJob(string pdfPath, int pageCount)
    {
        var job = new FileJobViewModel(pdfPath, Settings.QuickSuffixes)
        {
            OwnerFeatureId = "scan.page",
        };
        job.RenameRequested += OnJobRenameRequested;
        job.SuffixToggleRequested += OnJobSuffixToggleRequested;
        job.Apply(FeatureResult.Success(pdfPath, pdfPath, $"扫描 {pageCount} 页", pageCount));
        Board.Add(job);
        Board.MoveToTop(job);
        // Zero-click preview: the strip unfolds the moment the scan lands.
        _preview.Open(job);
        return job;
    }

    /// <summary>
    /// Fire-and-forget content naming for a fresh scan: the row is already visible under its
    /// timestamp name; when the chain lands a confident name the file moves and the row follows
    /// (the open strip re-finds its file by fingerprint when it next needs the path). Naming
    /// must never fail the scan — every exception lands in the log, the timestamp name stays.
    /// </summary>
    private async Task RenameScanInBackgroundAsync(FileJobViewModel job, string pdfPath)
    {
        try
        {
            // The open strip auto-orients / rewrites the file right after landing; a rename
            // (File.Move) during that write throws, so drain the strip's busy passes first.
            await (job.Strip?.WaitForSettledAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            string finalPath = await _naming.TryRenameAsync(pdfPath).ConfigureAwait(false);
            if (string.Equals(finalPath, pdfPath, StringComparison.OrdinalIgnoreCase)) return;
            AppLog.Info($"后台命名完成：{Path.GetFileName(pdfPath)} → {Path.GetFileName(finalPath)}");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.Invoke(() => job.UpdateRenamed(finalPath));
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：后台自动命名异常，保留时间戳命名", ex);
        }
    }

    private void SaveSettings()
    {
        // The pipeline preference may have changed: repoint the proxy before persisting so the
        // next job already uses the newly chosen layout block and engine.
        AppOcrEngineChain.ApplyPipeline(Settings.PreferredLayoutStage, Settings.PreferredOcrEngine);

        if (SettingsService.Save(Settings))
        {
            AppLog.Info("设置已保存");
            // Quick suffixes may have changed: refresh the chips of every existing job.
            Board.RefreshSuffixChips(Settings.QuickSuffixes);
        }
        else
            AppLog.Error("设置保存失败（目录不可写？）");
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

    private void OnJobRenameRequested(object? sender, string desiredName)
    {
        if (sender is FileJobViewModel job) _renamer.Rename(job, desiredName);
    }

    private void OnJobSuffixToggleRequested(object? sender, string suffix)
    {
        if (sender is FileJobViewModel job) _renamer.ApplySuffix(job, suffix);
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
    /// the 手机传文件 section, so rename, suffix chips, delete and open-folder all work unchanged.</summary>
    private void OnTransferFileReceived(string path, long bytes)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) AddTransferJob(path, bytes);
        else dispatcher.Invoke(() => AddTransferJob(path, bytes));
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
        job.RenameRequested += OnJobRenameRequested;
        job.SuffixToggleRequested += OnJobSuffixToggleRequested;
        job.Apply(FeatureResult.Success(path, path, $"来自手机 · {size}", 1));
        Board.Add(job);
        Board.MoveToTop(job);
    }

    /// <summary>Worker thread → UI thread: the PDF assembled from a CamScanner share link runs
    /// through the same content-based auto-naming as scans (「扫描全能王 2026-9-30 14.36.pdf」
    /// becomes 「客户姓名+文件类型.pdf」 when a page is confidently recognised; any failure keeps
    /// the timestamp name), then joins the job list as a finished record.</summary>
    private void OnTransferShareDocumentReceived(string path)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.InvokeAsync(() => AddTransferJobFromLinkAsync(path));
    }

    private async Task AddTransferJobFromLinkAsync(string path)
    {
        string finalPath = await _naming.TryRenameAsync(path).ConfigureAwait(true);
        long bytes = 0;
        try { bytes = new FileInfo(finalPath).Length; } catch { /* best effort — size display only */ }
        AddTransferJob(finalPath, bytes);
    }

    /// <summary>Stops the LAN receiver; the window calls this on close so the port frees at once.</summary>
    public void Shutdown() => Transfer.Stop();
}
