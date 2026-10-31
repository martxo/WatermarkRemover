using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Presentation;
using WatermarkRemover.Pdf.Rendering;
using WatermarkRemover.Ocr;
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
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>
    /// Built once outside the UI layer; the shell only consumes it. One shared OCR engine and one
    /// shared renderer live inside it (the WeChat OCR worker is a single channel, so both features
    /// must go through the same proxy).
    /// </summary>
    private readonly AppComposition _composition;

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
    /// is optional so a test run never reads or writes the machine's real job history; the
    /// composition is injectable so a test can swap engines/renderers without touching the UI.</summary>
    internal MainViewModel(
        IDialogService dialogs,
        IShellOpenService shell,
        TaskJournal? journal = null,
        AppComposition? composition = null)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _journal = journal;
        _composition = composition ?? AppComposition.Shared;

        Settings = SettingsService.Load();
        // Honour the pipeline pinned in settings (empty = automatic priority chains).
        AppOcrEngineChain.ApplyPipeline(Settings.PreferredLayoutStage, Settings.PreferredOcrEngine);
        Board = new JobBoard(_dialogs);
        NavItems = new ObservableCollection<FeatureNavItemViewModel>(
            _composition.Registry.Features.Select(f => new FeatureNavItemViewModel(f)));
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
        _naming = new ContentNamingService(Settings, _composition.PdfRenderer, _composition.OcrEngine);
        _renamer = new JobRenamer(_dialogs);
        _preview = new PreviewStripCoordinator(
            Board.Jobs, _dialogs, _composition.PdfRenderer, _composition.OcrEngine,
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

        // --- 截图 ---
        // The overlay lives in the shell; this page keeps the launcher, hotkey state and records.
        CaptureScreenshotCommand = new RelayCommand(
            () => _ = CaptureScreenshotAsync(), () => !_screenshotBusy);
        CaptureFullScreenCommand = new RelayCommand(
            () => _ = CaptureFullScreenAsync(0), () => !_screenshotBusy);
        CaptureDelayedFullScreenCommand = new RelayCommand(
            () => _ = CaptureFullScreenAsync(3), () => !_screenshotBusy);
        OpenScreenshotFolderCommand = new RelayCommand(OpenScreenshotFolder);

        // --- 手机传文件 ---
        // The LAN receiver lives as long as the app; landed files become finished job rows in
        // the 手机传文件 section (rename / chips / delete / open-folder all work unchanged).
        Transfer = new TransferPanelViewModel(
            Settings, _shell, message => AppLog.Trace(message, null), _composition.OcrEngine);
        Transfer.FileReceived += OnTransferFileReceived;
        Transfer.ShareDocumentReceived += OnTransferShareDocumentReceived;

        // --- 快速搜索 ---
        // The page asks the local index host over a named pipe. The index itself lives in a
        // privileged background process, so this app never needs elevation to search.
        Search = new SearchPanelViewModel(
            new IndexServiceSearchProvider(), _shell, DialogService.Default, RecycleBinService.Default);

        // Last, once every service the restored rows talk to (netdisk accounts included) exists.
        RestoreJournal();
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

    /// <summary>
    /// Icon / caption / page kind of the selected feature. The shell binds here instead of on the
    /// feature itself: those are UI facts and live in the presentation catalog, not in Core.
    /// </summary>
    public FeaturePresentation SelectedPresentation => FeaturePresentationCatalog.For(SelectedFeature);

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
        OnPropertyChanged(nameof(SelectedPresentation));
        Board.SetSection(_selectedFeature.Id, _selectedFeature.DisplayName);
        OnPropertyChanged(nameof(DropHint));
        OnPropertyChanged(nameof(FilePickerFilter));
        OnPropertyChanged(nameof(FilePickerTitle));
        OnPropertyChanged(nameof(EmptyJobsText));
        // Each section owns its own upload batch: the footer button follows the page.
        OnPropertyChanged(nameof(CanUploadBatch));
        OnPropertyChanged(nameof(UploadBatchHint));
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
}
