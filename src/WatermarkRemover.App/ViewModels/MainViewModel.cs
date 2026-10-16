using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// Root view model. Dropping files triggers the selected feature immediately; results are pushed
/// into the observable queue as each job finishes. This class holds no feature logic of its own.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>One shared engine: the WeChat OCR worker is a single channel, so both features
    /// must go through the same instance to keep recognition serialised.</summary>
    private static readonly IOcrEngine SharedOcrEngine = CreateOcrEngine();

    private readonly FeatureRegistry _registry = new(new IFeature[]
    {
        new WatermarkFeature(
            new PdfDocumentSource(),
            new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            new PdfiumPdfPageRenderer(),
            SharedOcrEngine,
            trace: (message, ex) =>
            {
                if (ex is null) AppLog.Info(message);
                else AppLog.Error(message, ex);
            }),
        new OcrFeature(new PdfiumPdfPageRenderer(), SharedOcrEngine),
    });

    private IFeature _selectedFeature;
    private readonly object _sync = new();
    private readonly IDialogService _dialogs;
    private readonly IShellOpenService _shell;
    private readonly JobQueueService _queue = new();
    private bool _isBusy;
    private int _completedCount;

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
        Jobs = new ObservableCollection<FileJobViewModel>();
        NavItems = new ObservableCollection<FeatureNavItemViewModel>(
            _registry.Features.Select(f => new FeatureNavItemViewModel(f, GlyphFor(f.Id))));
        NavItems[0].IsSelected = true;
        _selectedFeature = NavItems[0].Feature;

        ClearCommand = new RelayCommand(ClearJobs, () => Jobs.Count > 0 && !IsBusy);
        OpenOutputCommand = new RelayCommand(p => OpenOutput(p as string), p => p is string path && File.Exists(path));
        DeleteJobCommand = new RelayCommand(DeleteJob, p => p is FileJobViewModel && !IsBusy);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
    }

    /// <summary>Logs the resolved engine (and every skipped link) once per process.</summary>
    private static IOcrEngine CreateOcrEngine()
    {
        foreach (OcrEngineLink link in AppOcrEngineChain.Instance.Links)
        {
            if (link.Available)
                AppLog.Info($"OCR 引擎：{link.Name}");
            else
                AppLog.Info($"未启用 {link.Name}：{link.Reason}");
        }

        return AppOcrEngineChain.Instance.Engine;
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
            OnPropertyChanged(nameof(StatusText));
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
                OnPropertyChanged(nameof(StatusText));
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
        _ => "\uE7C3",
    };

    public ObservableCollection<FileJobViewModel> Jobs { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand DeleteJobCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

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
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public int CompletedCount
    {
        get => _completedCount;
        private set
        {
            if (SetProperty(ref _completedCount, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => IsBusy
        ? $"处理中… 已完成 {CompletedCount}/{Jobs.Count}"
        : Jobs.Count == 0
            ? $"拖入 PDF 文件即可开始{SelectedFeature.DisplayName}"
            : $"已完成 {CompletedCount} 个文件";

    /// <summary>Entry point for drag-and-drop and the file picker.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        string[] files = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

        if (files.Length == 0) return;

        AppLog.Info($"收到 {files.Length} 个待处理文件");

        var newJobs = files.Select(f =>
        {
            var job = new FileJobViewModel(f, Settings.QuickSuffixes);
            job.RenameRequested += OnJobRenameRequested;
            job.SuffixToggleRequested += OnJobSuffixToggleRequested;
            return job;
        }).ToList();
        lock (_sync)
        {
            foreach (var job in newJobs) Jobs.Add(job);
            CompletedCount = 0;
        }
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

    private void ClearJobs()
    {
        if (IsBusy) return;
        Jobs.Clear();
        CompletedCount = 0;
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
        OnPropertyChanged(nameof(StatusText));
    }

    private void SaveSettings()
    {
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
