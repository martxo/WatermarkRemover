using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
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
    private bool _isBusy;
    private int _completedCount;

    public MainViewModel()
    {
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

    /// <summary>
    /// Engine chain, best quality first: WeChat's local engine recognises Chinese documents best,
    /// then the bundled offline PP-OCR models (for machines without WeChat), then the OCR built
    /// into Windows. Each step degrades safely — a machine missing one engine simply falls through
    /// to the next instead of failing the job.
    /// </summary>
    private static IOcrEngine CreateOcrEngine()
    {
        if (WeChatOcrEngine.TryLocate(out WeChatOcrEngine? wechat, out string wechatReason))
        {
            AppLog.Info("OCR 引擎：微信本地 OCR");
            return wechat!;
        }

        AppLog.Info($"未启用微信 OCR 引擎：{wechatReason}");

        if (PpOcrEngine.TryLocate(out PpOcrEngine? ppocr, out string ppocrReason))
        {
            AppLog.Info("OCR 引擎：PP-OCR（内置离线模型）");
            return ppocr!;
        }

        AppLog.Info($"未启用 PP-OCR 引擎：{ppocrReason}");

        try
        {
            var windows = new WindowsMediaOcrEngine();
            AppLog.Info($"OCR 引擎：Windows 原生 OCR（可用：{windows.IsAvailable}）");
            return windows;
        }
        catch (Exception ex)
        {
            // The WinRT OCR stack itself is missing/broken on some systems (REGDB_E_CLASSNOTREG);
            // keep the chain alive with a stub so the failure is reported per job, not at startup.
            AppLog.Error("Windows 原生 OCR 初始化失败", ex);
            return new UnavailableOcrEngine($"微信 OCR 不可用（{wechatReason}）；PP-OCR 不可用（{ppocrReason}）；Windows OCR 初始化失败：{ex.Message}");
        }
    }

    /// <summary>Terminal fallback: every call fails with the full diagnosis of the chain above.</summary>
    private sealed class UnavailableOcrEngine : IOcrEngine
    {
        private readonly string _reason;

        public UnavailableOcrEngine(string reason) => _reason = reason;

        public string DisplayName => "无可用OCR引擎";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            throw new InvalidOperationException($"此电脑没有可用的 OCR 引擎。\n{_reason}");
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
            using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var tasks = jobs.Select(async job =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ProcessOneAsync(job, feature, context).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ProcessOneAsync(FileJobViewModel job, IFeature feature, FeatureContext context)
    {
        return Task.Run(() =>
        {
            job.MarkProcessing();
            try
            {
                if (!feature.CanHandle(job.InputPath))
                {
                    job.Apply(FeatureResult.Fail(job.InputPath, $"{feature.DisplayName}只支持 PDF 文件"));
                    return;
                }

                FeatureResult result = feature.Run(job.InputPath, context);
                job.Apply(result);
                LogResult(result, feature);
            }
            catch (Exception ex)
            {
                // Features never throw for expected failures; this guards the queue against
                // unexpected defects so one bad file cannot stall the whole batch.
                AppLog.Error($"处理意外异常：{job.InputPath}", ex);
                job.Apply(FeatureResult.Fail(job.InputPath, $"意外错误：{ex.Message}", ex, feature.DisplayName));
            }
            finally
            {
                lock (_sync) CompletedCount++;
                MoveToTop(job);
            }
        });
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

    private static void LogResult(FeatureResult result, IFeature feature)
    {
        string message = result.Status switch
        {
            FeatureStatus.Succeeded =>
                $"{feature.DisplayName}完成：{result.InputPath} → {result.OutputPath}（{result.Summary}）",
            FeatureStatus.NoChange => $"{feature.DisplayName}无输出：{result.InputPath}（{result.Summary}）",
            _ => $"{feature.DisplayName}失败：{result.InputPath}（{result.Error}）",
        };
        AppLog.Info(message);
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
        MessageBoxResult confirm = MessageBox.Show(message, "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

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
            MessageBox.Show($"删除文件失败：{ex.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show($"重命名失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            // Reveal the file in Explorer rather than opening the PDF viewer.
            string? directory = Path.GetDirectoryName(path);
            if (directory != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开输出目录：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
