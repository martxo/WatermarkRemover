using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Naming;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Pdf;
using WatermarkRemover.Scan.Core.Models;

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
        new WatermarkFeature(
            new PdfDocumentSource(),
            new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            SharedPdfRenderer,
            SharedOcrEngine,
            trace: AppLog.Trace),
        new OcrFeature(SharedPdfRenderer, SharedOcrEngine, trace: AppLog.Trace),
        new NetdiskFeature(),
    });

    private IFeature _selectedFeature;
    private readonly object _sync = new();
    private readonly IDialogService _dialogs;
    private readonly IShellOpenService _shell;
    private readonly JobQueueService _queue = new();
    private readonly ScanService _scanService;

    /// <summary>Serializes every scanner run — the hardware is exclusive, and the scan page's
    /// button and the preview strips' gap buttons must never drive it at the same time.</summary>
    private readonly SemaphoreSlim _scannerGate = new(1, 1);
    private bool _isBusy;
    private int _completedCount;
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
        Jobs = new ObservableCollection<FileJobViewModel>();
        NavItems = new ObservableCollection<FeatureNavItemViewModel>(
            _registry.Features.Select(f => new FeatureNavItemViewModel(f, GlyphFor(f.Id))));
        NavItems[0].IsSelected = true;
        _selectedFeature = NavItems[0].Feature;

        ClearCommand = new RelayCommand(ClearJobs, () => VisibleJobCount > 0 && !IsBusy);
        OpenOutputCommand = new RelayCommand(p => OpenOutput(p as string), p => p is string path && File.Exists(path));
        DeleteJobCommand = new RelayCommand(DeleteJob, p => p is FileJobViewModel && !IsBusy);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ScanCommand = new RelayCommand(() => _ = RunScanAsync(), () => IsIdle);
        PreviewCommand = new RelayCommand(p => TogglePreviewStrip(p as FileJobViewModel), p => p is FileJobViewModel);
        _scanService = new ScanService(Settings);

        // --- 网盘 ---
        // Products are discovered, so a new netdisk product shows up in the UI and the settings
        // form with no change here. Accounts decode from settings (secrets via DPAPI).
        NetdiskCatalog = new NetdiskProviderCatalog();
        NetdiskCatalog.RegisterDiscovered(typeof(WatermarkRemover.Netdisk.AliyunDriveProvider).Assembly, AppLog.Trace);
        foreach (var account in NetdiskAccountPersistence.Decode(Settings.NetdiskAccounts, new DpapiSecretProtector(), AppLog.Trace))
            NetdiskAccounts.Add(account);
        NetdiskSettings = new NetdiskAccountSettingViewModel(
            NetdiskCatalog,
            NetdiskAccounts,
            UpsertNetdiskAccount,
            (providerId, name) => RemoveNetdiskAccount(providerId, name),
            AppLog.Trace);
        OpenNetdiskCommand = new RelayCommand(OpenNetdisk);
        QuickUploadCommand = new RelayCommand(() => _ = QuickUploadAsync(), () => !IsBusy);
        RefreshNetdiskSummary();
    }

    /// <summary>Logs the resolved engine (and every skipped link) once per process and returns
    /// the switchable proxy the features keep for their whole lifetime.</summary>
    private static IOcrEngine CreateOcrEngine()
    {
        foreach (OcrEngineLink link in AppOcrEngineChain.Instance.Links)
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
            OnPropertyChanged(nameof(FeatureTagline));
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
                OnPropertyChanged(nameof(FeatureTagline));
                OnSelectedFeatureChanged();
            }
        }
    }

    public string AppVersion =>
        "v" + (System.Reflection.Assembly.GetEntryAssembly()
            ?.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>Maps feature ids to Segoe MDL2 glyphs; unknown features get a generic page icon.</summary>
    private static string GlyphFor(string featureId) => featureId switch
    {
        "watermark.badge" => "\uE75C", // Erase
        "ocr.text" => "\uE7C3",        // Page
        "scan.page" => "\uE7F4",       // Camera
        "netdisk.page" => "\uE753",   // Cloud
        _ => "\uE7C3",
    };

    /// <summary>One-line capability note shown beside the page title; new features add a line
    /// here (or get the empty default) — nothing else in the shell needs to change.</summary>
    public string FeatureTagline => _selectedFeature.Id switch
    {
        "scan.page" => "自动命名 · 自动调正 · 自动删白",
        "watermark.badge" => "拖入 PDF → 去水印 · 智能命名 · 页面管理",
        "ocr.text" => "拖入 PDF → 逐页识别文字 → 生成同名 txt",
        "netdisk.page" => "网盘账号管理 · 上传下载 · 快捷传",
        _ => string.Empty,
    };

    public ObservableCollection<FileJobViewModel> Jobs { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand DeleteJobCommand { get; }

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
    public Action<PdfPreviewViewModel>? PreviewViewerOpener { get; set; }

    /// <summary>The settings page view model; non-null only while the page is open.</summary>
    public SettingsViewModel? SettingsPage
    {
        get => _settingsPage;
        private set => SetProperty(ref _settingsPage, value);
    }

    /// <summary>Enters the settings second-level page (fresh working copy each time).</summary>
    public void OpenSettings()
    {
        var page = new SettingsViewModel(Settings, _dialogs, _shell, netdisk: NetdiskSettings);
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
                OnPropertyChanged(nameof(StatusText));
                ClearCommand.RaiseCanExecuteChanged();
                DeleteJobCommand.RaiseCanExecuteChanged();
                ScanCommand.RaiseCanExecuteChanged();
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

    public int CompletedCount
    {
        get => _completedCount;
        private set
        {
            if (SetProperty(ref _completedCount, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Records of the current section only — every page shows its own history,
    /// scan runs never surface inside 去水印 and vice versa.</summary>
    public ICollectionView JobsView
    {
        get
        {
            if (_jobsView is null)
            {
                _jobsView = CollectionViewSource.GetDefaultView(Jobs);
                _jobsView.Filter = o => o is FileJobViewModel j && j.OwnerFeatureId == SelectedFeature.Id;
            }
            return _jobsView;
        }
    }

    private ICollectionView? _jobsView;

    /// <summary>Number of records visible on the current section (status bar + 清除 gate).</summary>
    public int VisibleJobCount => Jobs.Count(j => j.OwnerFeatureId == SelectedFeature.Id);

    public string StatusText => IsBusy
        ? $"处理中… 已完成 {CompletedCount}/{VisibleJobCount}"
        : VisibleJobCount == 0
            ? $"拖入 PDF 文件即可开始「{SelectedFeature.DisplayName}」"
            : $"已完成 {CompletedCount} 个文件";

    /// <summary>Section switch: swap the view filter without rebuilding the underlying data.</summary>
    private void OnSelectedFeatureChanged()
    {
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DropHint));
        if (_jobsView is not null) _jobsView.Refresh();
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
        lock (_sync)
        {
            foreach (var job in newJobs) Jobs.Add(job);
            CompletedCount = 0;
        }
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));

        await ProcessJobsAsync(newJobs, SelectedFeature).ConfigureAwait(true);
    }

    private async Task ProcessJobsAsync(IReadOnlyList<FileJobViewModel> jobs, IFeature feature)
    {
        IsBusy = true;
        try
        {
            // Snapshot once per batch: background threads read this copy, the settings window
            // keeps editing the live object — the two can never interfere.
            var context = FeatureContext.Capture(Settings);
            int maxConcurrency = Math.Clamp(Settings.MaxConcurrency, 1, 8);

            await _queue.RunAsync(jobs, feature, context, maxConcurrency, job =>
            {
                lock (_sync) CompletedCount++;
                MoveToTop(job);
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
            outcome = await ScanOnceAsync(progressSink: null).ConfigureAwait(true);
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

    /// <summary>
    /// The one scanner session every scan goes through, however it was started — the scan
    /// page's 开始扫描 button and the preview strip's gap buttons both land here, so the
    /// busy state and status line behave identically for both. It takes the scanner gate
    /// (fails fast while the hardware is busy), lifts <see cref="IsBusy"/> so every command
    /// sees the scanner working, maps the scan page's layout/colour, and mirrors progress
    /// into the scan panel's status line; <paramref name="progressSink"/>, when given,
    /// receives the same messages (the preview strip paints them under its record card).
    /// Throws <see cref="ScannerBusyException"/> when another scan still holds the hardware.
    /// </summary>
    private async Task<ScanOutcome> ScanOnceAsync(IProgress<string>? progressSink)
    {
        if (!await _scannerGate.WaitAsync(0).ConfigureAwait(true))
            throw new ScannerBusyException();

        IsBusy = true;
        ScanStatus = "正在准备扫描…";
        var progress = new Progress<string>(message =>
        {
            ScanStatus = message;
            progressSink?.Report(message);
        });
        try
        {
            var (duplex, color) = ScanModes();
            return await _scanService.ScanAsync(duplex, color, progress, CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            _scannerGate.Release();
            IsBusy = false;
            ScanStatus = string.Empty;
        }
    }

    /// <summary>The scan page's layout and colour, mapped from the settings' UI ints.</summary>
    private (DuplexMode Duplex, ColorMode Color) ScanModes()
    {
        var duplex = (DuplexMode)Math.Clamp(Settings.ScanDuplex, 0, 2);
        // UI ints (0 自动 / 1 彩色 / 2 灰度 / 3 黑白) deliberately differ from the engine enum's
        // numeric order (GrayDiffusion sits between Color24 and TrueGray) — map explicitly.
        var color = Settings.ScanColor switch
        {
            0 => ColorMode.Auto,
            1 => ColorMode.Color24,
            2 => ColorMode.TrueGray,
            _ => ColorMode.BlackWhite,
        };
        return (duplex, color);
    }

    /// <summary>
    /// The preview strip's gap-button scan: one pass handed back as the scanned PDF's path for
    /// the strip to splice in. It runs the exact same scanner session as the scan page's
    /// 开始扫描 button (<see cref="ScanOnceAsync"/>) — gate, busy state, layout/colour and
    /// progress mirroring included — so the two entries cannot drift apart again.
    /// </summary>
    private async Task<string?> ScanIntoStripAsync(IProgress<string> progress)
    {
        ScanOutcome outcome = await ScanOnceAsync(progress).ConfigureAwait(true);
        if (!outcome.Success)
        {
            AppLog.Error($"插入扫描失败：{outcome.Error}");
            throw new InvalidOperationException(outcome.Error ?? "扫描失败");
        }
        return outcome.OutputPath;
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
        Jobs.Add(job);
        MoveToTop(job);
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
        // Zero-click preview: the strip unfolds the moment the scan lands.
        OpenPreviewStrip(job);
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
            string finalPath = await TryRenameScanByContentAsync(pdfPath).ConfigureAwait(false);
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

    /// <summary>Render resolution for the scan naming OCR pass. 220 dpi: at 150 the value
    /// cells of watermark-pressed documents (credit report header tables) came back empty —
    /// only the labels survived, so no name could be extracted.</summary>
    private const int NamingRenderDpi = 220;

    /// <summary>Cap on the pages probed for naming: a duplex/multi-sheet scan packs several
    /// document faces into one PDF and the type may only sit on a later page, but a long
    /// unrecognisable document must not hold the job row hostage with serial OCR passes. The
    /// settings can pin probing to the first page only — the common case reads the name from
    /// page one, and a long document would just burn seconds.</summary>
    private const int MaxNamingProbePages = 4;

    /// <summary>Type short name for the fixed-region credit report locator's naming result.</summary>
    private static readonly string CreditReportTypeShortName =
        DocumentProfileCatalog.All.First(p => p.TypeId == "credit-report").TypeShortName;

    /// <summary>
    /// Content-based rename for a fresh scan. Fixed-layout types (credit report) go through
    /// the region locator first: crop the top band, find the「被查询者姓名」label box, OCR only
    /// the cell below it — no full-page recognition at all. Other types reuse the watermark
    /// naming chain (render page → OCR → SmartNamingService):「扫描yyyyMMdd-HHmmss」becomes
    /// 「客户姓名+文件类型」when a page is confidently recognised. Pages are probed in order up
    /// to <see cref="MaxNamingProbePages"/> because the first face of a duplex scan is often a
    /// blank back or a different document. Any failure keeps the timestamp name — scanning must
    /// never fail because of naming — and every skip is traced so the log explains a missed
    /// rename. Returns the path to file into the job list.
    /// </summary>
    private async Task<string> TryRenameScanByContentAsync(string pdfPath)
    {
        if (!Settings.AutoNameByContent) return pdfPath;

        try
        {
            int pageCount = await SharedPdfRenderer.GetPageCountAsync(pdfPath).ConfigureAwait(true);
            int probeLimit = Settings.NameFromFirstPageOnly ? 1 : MaxNamingProbePages;
            AutoNameResult? autoName = null;
            for (int pageIndex = 0; pageIndex < pageCount && pageIndex < probeLimit && autoName is null; pageIndex++)
            {
                byte[] image = await SharedPdfRenderer.RenderPageAsync(pdfPath, pageIndex, NamingRenderDpi).ConfigureAwait(true);
                // 摆正后再识别：ADF 躺倒页在方向检测完成前就进命名链会取错字（横版画布守卫不受影响）。
                image = await new PdfOrientationDetector(SharedOcrEngine).OrientAsync(image).ConfigureAwait(true);
                AppLog.Trace($"命名：第 {pageIndex + 1}/{pageCount} 页渲染 {image.Length / 1024} KB", null);

                // Fast path for fixed-layout types: locate the name cell geometrically and
                // recognise only that crop — no full-page OCR, no text-order guessing.
                string? located = await CreditReportNameLocator
                    .TryLocateNameAsync(image, SharedOcrEngine, AppLog.Trace)
                    .ConfigureAwait(true);
                if (located is not null)
                {
                    autoName = AutoNameResult.Create(located, CreditReportTypeShortName);
                    AppLog.Trace($"命名：自动命名成功 → {autoName.SuggestedStem}", null);
                    continue;
                }

                IReadOnlyList<RecognizedLine> lines = await SharedOcrEngine
                    .RecognizeBoundedOrDefaultAsync(image).ConfigureAwait(true);
                AppLog.Trace(
                    $"命名：第 {pageIndex + 1}/{pageCount} 页 OCR 识别 {lines.Count} 行：{string.Join(" / ", lines.Select(l => l.Text))}",
                    null);
                autoName = SmartNamingService.TryBuildName(lines, AppLog.Trace);
            }
            if (autoName is null) return pdfPath;

            string directory = Path.GetDirectoryName(pdfPath)!;
            string target = OutputNameGenerator.FindAvailablePath(directory, autoName.SuggestedStem + Path.GetExtension(pdfPath));
            if (string.Equals(target, pdfPath, StringComparison.OrdinalIgnoreCase)) return pdfPath;

            // The strip may still hold the file for a page write (e.g. the user rotated a page
            // while the naming OCR ran); a short backoff beats losing the good name.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(pdfPath, target);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(attempt * 400).ConfigureAwait(false);
                }
            }
            AppLog.Info($"扫描自动命名：{Path.GetFileName(pdfPath)} → {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：扫描自动命名过程异常，保留时间戳命名", ex);
            return pdfPath;
        }
    }

    /// <summary>
    /// Moves a finished job to the top of the list so the most recently processed file stays on
    /// top. Must be safe to call from worker threads — collection changes are marshaled to the UI.
    /// </summary>
    private void MoveToTop(FileJobViewModel job)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Move()
        {
            int index = Jobs.IndexOf(job);
            if (index > 0) Jobs.Move(index, 0);
        }

        if (dispatcher.CheckAccess()) Move();
        else dispatcher.Invoke(Move);
    }

    /// <summary>Clears the current section's records only — each page owns its own history.</summary>
    private void ClearJobs()
    {
        if (IsBusy) return;
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (Jobs[i].OwnerFeatureId == SelectedFeature.Id)
                Jobs.RemoveAt(i);
        }
        CompletedCount = 0;
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Removes one finished record from the list after a single confirmation. The generated
    /// output file goes to the Recycle Bin (recoverable); the original input file is never touched.
    /// </summary>
    private void DeleteJob(object? parameter)
    {
        if (parameter is not FileJobViewModel job) return;

        bool hasOutput = job.OutputPath is not null && File.Exists(job.OutputPath);
        string message = hasOutput
            ? $"确定要删除「{job.DisplayName}」吗？\n\n已生成的文件将移入回收站，源文件不受影响。"
            : $"确定要删除「{job.FileName}」这条记录吗？";
        if (!_dialogs.Confirm(message, "删除确认")) return;

        try
        {
            if (hasOutput)
            {
                FileSystem.DeleteFile(job.OutputPath!, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                AppLog.Info($"已删除生成文件：{job.OutputPath}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"删除生成文件失败：{job.OutputPath}", ex);
            _dialogs.Error($"删除文件失败：{ex.Message}", "删除失败");
            return;
        }

        Jobs.Remove(job);
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
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
            foreach (var job in Jobs)
                job.RefreshSuffixChips(Settings.QuickSuffixes);
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
    }

    private void OnJobRenameRequested(object? sender, string desiredName)
    {
        if (sender is FileJobViewModel job) RenameJob(job, desiredName);
    }

    private void OnJobSuffixToggleRequested(object? sender, string suffix)
    {
        if (sender is not FileJobViewModel job) return;
        RenameJob(job, OutputNameEditor.WithSuffix(job.DisplayName, suffix));
    }

    /// <summary>
    /// Renames the generated output file on disk. Name rules come from <see cref="OutputNameEditor"/>;
    /// a name collision is resolved by counting up, never by overwriting.
    /// </summary>
    private void RenameJob(FileJobViewModel job, string desiredName)
    {
        if (job.OutputPath is null) return;

        string currentName = Path.GetFileName(job.OutputPath);
        string? newName = OutputNameEditor.BuildRenamedName(currentName, desiredName);
        if (newName is null || string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
            return;

        string directory = Path.GetDirectoryName(job.OutputPath)!;
        try
        {
            string target = OutputNameGenerator.FindAvailablePath(directory, newName);
            File.Move(job.OutputPath, target);

            AppLog.Info($"重命名输出文件：{currentName} → {Path.GetFileName(target)}");
            job.UpdateRenamed(target);
        }
        catch (Exception ex)
        {
            AppLog.Error($"重命名失败：{job.OutputPath} → {newName}", ex);
            _dialogs.Warn($"重命名失败：{ex.Message}", "提示");
        }
    }

    /// <summary>
    /// Expands (or folds) the thumbnail strip under a finished record. The strip view model is
    /// built once per row and reused for every later open, so the row, its strip and the enlarge
    /// overlay all drive one shared instance — one render cache, one rotation lock, one truth.
    /// </summary>
    private void TogglePreviewStrip(FileJobViewModel? job)
    {
        if (job is null) return;

        // Built already: fold/unfold purely from state — the cached tiles keep working even if
        // the file vanished externally, and a strip must always be foldable again.
        if (job.Strip is not null)
        {
            job.StripOpen = !job.StripOpen;
            AppLog.Info(job.StripOpen ? $"打开预览：{job.OutputPath}" : "收起预览");
            return;
        }

        OpenPreviewStrip(job);
    }

    /// <summary>Builds and unfolds the row's strip immediately. Scans call this the moment the
    /// PDF lands (zero-click preview); other sections reach it through the preview button.</summary>
    internal void OpenPreviewStrip(FileJobViewModel job)
    {
        string? path = job.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Info("这条记录没有可预览的 PDF 文件。", "提示");
            return;
        }

        // Scan rows (this method's zero-click entry) self-correct their orientation after the
        // preview loads; files opened via the preview button keep the manual button only.
        bool scanRow = job.OwnerFeatureId == "scan.page";
        var strip = new PdfPreviewViewModel(
            path, Path.GetFileName(path), SharedPdfRenderer,
            confirmPageDelete: index => _dialogs.Confirm(
                $"确定要删除第 {index + 1} 页吗？\n\n该页将从 PDF 中移除并立即保存，删除后无法恢复。",
                "删除确认"),
            ocrEngine: SharedOcrEngine,
            autoOrient: scanRow,
            onRelocated: job.UpdateRenamed, // a renamed file is tracked, the row follows
            autoCleanBlank: scanRow, // duplex scans drop their blank back sides by themselves
            scanProvider: scanRow ? ScanIntoStripAsync : null); // scan rows can insert scans into gaps
        strip.EnlargeRequested += OnEnlargeRequested;
        job.AttachStrip(strip);
        AppLog.Info($"打开预览：{path}");
        _ = strip.LoadAsync();
    }

    /// <summary>
    /// A tile was clicked: the host opens the screen-level viewer on this strip (the shared view
    /// model means the viewer shows exactly the clicked page and its rail stays in sync).
    /// </summary>
    private void OnEnlargeRequested(object? sender, EventArgs e)
    {
        if (sender is not PdfPreviewViewModel page) return;

        AppLog.Info($"打开看图模式：{page.Title}");
        PreviewViewerOpener?.Invoke(page);
    }

    private void OpenOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        // Reveal the file in Explorer rather than opening the PDF viewer.
        string? directory = Path.GetDirectoryName(path);
        if (directory is null) return;

        if (!_shell.TryOpenDirectory(directory, out string? error))
            _dialogs.Info($"无法打开输出目录：{error}", "提示");
    }

    // --- 网盘 ---------------------------------------------------------

    /// <summary>Registered netdisk products (discovered from the WatermarkRemover.Netdisk assembly).</summary>
    public NetdiskProviderCatalog NetdiskCatalog { get; }

    /// <summary>Configured 网盘 accounts, plaintext in memory; secrets are protected on save.</summary>
    public ObservableCollection<NetdiskAccountState> NetdiskAccounts { get; } = new();

    /// <summary>The 「网盘设置」 group state: dynamic credential form, test connection, OAuth.</summary>
    public NetdiskAccountSettingViewModel NetdiskSettings { get; }

    public RelayCommand OpenNetdiskCommand { get; }

    public RelayCommand QuickUploadCommand { get; }

    /// <summary>Inserts or replaces the account with the same product + name, then persists.</summary>
    public void UpsertNetdiskAccount(NetdiskAccountState account)
    {
        var existing = NetdiskAccounts.FirstOrDefault(a => a.ProviderId == account.ProviderId && a.Name == account.Name);
        if (existing is null)
        {
            NetdiskAccounts.Add(account);
        }
        else
        {
            NetdiskAccounts[NetdiskAccounts.IndexOf(existing)] = account;
        }

        PersistNetdiskAccounts();
        RefreshNetdiskSummary();
    }

    /// <summary>Removes the account and persists; returns whether anything was removed.</summary>
    public bool RemoveNetdiskAccount(string providerId, string name)
    {
        var removed = NetdiskAccounts
            .Where(a => a.ProviderId == providerId && a.Name == name)
            .ToList();
        foreach (var account in removed)
        {
            NetdiskAccounts.Remove(account);
        }

        if (removed.Count > 0)
        {
            PersistNetdiskAccounts();
            RefreshNetdiskSummary();
        }

        return removed.Count > 0;
    }

    /// <summary>Secrets go through DPAPI before they reach the settings file.</summary>
    private void PersistNetdiskAccounts()
    {
        Settings.NetdiskAccounts = NetdiskAccountPersistence.Encode(
            NetdiskAccounts, NetdiskCatalog.All, new DpapiSecretProtector());
        if (SettingsService.Save(Settings))
            AppLog.Info($"网盘账号已保存（{NetdiskAccounts.Count} 个）");
        else
            AppLog.Error("网盘账号保存失败（目录不可写？）");
    }

    private string _netdiskSummaryText = "未配置网盘账号。";

    private NetdiskWindow? _netdiskWindow;

    /// <summary>Status line shown on the 网盘 page.</summary>
    public string NetdiskSummaryText
    {
        get => _netdiskSummaryText;
        private set => SetProperty(ref _netdiskSummaryText, value);
    }

    private void RefreshNetdiskSummary()
    {
        NetdiskSummaryText = NetdiskAccounts.Count == 0
            ? "未配置网盘账号，请先在「设置 → 网盘设置」中添加。"
            : $"已配置 {NetdiskAccounts.Count} 个账号：{string.Join("、", NetdiskAccounts.Select(a => a.Name))}";
    }

    /// <summary>Opens the management window; a second click just activates the open one.</summary>
    private void OpenNetdisk()
    {
        if (_netdiskWindow is { IsLoaded: true })
        {
            _netdiskWindow.Activate();
            return;
        }

        _netdiskWindow = new NetdiskWindow(
            new NetdiskViewModel(NetdiskCatalog, NetdiskAccounts, UpsertNetdiskAccount, _dialogs, AppLog.Trace),
            NetdiskSettings)
        {
            Owner = Application.Current.MainWindow,
        };
        _netdiskWindow.Closed += (_, _) => _netdiskWindow = null;
        _netdiskWindow.Show();
        AppLog.Info("打开网盘管理窗口");
    }

    /// <summary>Upload picked files to the first configured account's root folder.</summary>
    private async Task QuickUploadAsync()
    {
        if (NetdiskAccounts.Count == 0)
        {
            NetdiskSummaryText = "未配置网盘账号，请先在「设置 → 网盘设置」中添加。";
            return;
        }

        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传到网盘的文件" };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var account = NetdiskAccounts[0];
        var provider = NetdiskCatalog.All.FirstOrDefault(p => p.Id == account.ProviderId);
        if (provider is null)
        {
            NetdiskSummaryText = $"未找到网盘产品「{account.ProviderId}」。";
            return;
        }

        var files = dialog.FileNames;
        NetdiskSummaryText = $"正在上传 {files.Length} 个文件到「{account.Name}」…";
        try
        {
            await using var session = await provider.ConnectAsync(account.ToCredentials());
            foreach (var path in files)
            {
                await session.UploadAsync(string.Empty, path);
                AppLog.Trace($"网盘快速上传：{path}", null);
            }

            // Tokens may have rotated during the upload — write the latest bag back.
            UpsertNetdiskAccount(new NetdiskAccountState(
                account.ProviderId, account.Name, session.CurrentCredentials.Values));
            NetdiskSummaryText = $"已上传 {files.Length} 个文件到「{account.Name}」（{provider.DisplayName}）。";
        }
        catch (Exception ex)
        {
            AppLog.Trace($"网盘快速上传失败：{ex.Message}", ex);
            NetdiskSummaryText = $"上传失败：{ex.Message}";
        }
    }
}
