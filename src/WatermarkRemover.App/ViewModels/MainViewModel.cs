using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Processing;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// Root view model. Dropping files triggers processing immediately; results are pushed into the
/// observable queue as each job finishes.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly PdfDocumentSource _source = new();
    private readonly object _sync = new();
    private bool _isBusy;
    private int _completedCount;

    public MainViewModel()
    {
        Settings = SettingsService.Load();
        Jobs = new ObservableCollection<FileJobViewModel>();

        ClearCommand = new RelayCommand(ClearJobs, () => Jobs.Count > 0 && !IsBusy);
        OpenOutputCommand = new RelayCommand(p => OpenOutput(p as string), p => p is string path && File.Exists(path));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
    }

    public AppSettings Settings { get; }

    public ObservableCollection<FileJobViewModel> Jobs { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

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
            ? "拖入 PDF 文件即可开始去水印"
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

        await ProcessJobsAsync(newJobs).ConfigureAwait(true);
    }

    private async Task ProcessJobsAsync(IReadOnlyList<FileJobViewModel> jobs)
    {
        IsBusy = true;
        try
        {
            int maxConcurrency = Math.Clamp(Settings.MaxConcurrency, 1, 8);
            using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var tasks = jobs.Select(async job =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ProcessOneAsync(job).ConfigureAwait(false);
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

    private Task ProcessOneAsync(FileJobViewModel job)
    {
        return Task.Run(() =>
        {
            job.MarkProcessing();
            try
            {
                var processor = new WatermarkProcessor(
                    _source,
                    new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
                    Settings);

                ProcessingResult result = processor.Process(job.InputPath);
                job.Apply(result);
                LogResult(result);
            }
            catch (Exception ex)
            {
                // The pipeline itself never throws for expected failures; this guards the queue
                // against unexpected defects so one bad file cannot stall the whole batch.
                AppLog.Error($"处理意外异常：{job.InputPath}", ex);
                job.Apply(ProcessingResult.Failed(job.InputPath, $"意外错误：{ex.Message}"));
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

    private static void LogResult(ProcessingResult result)
    {
        string message = result.Status switch
        {
            ProcessingStatus.Succeeded =>
                $"处理完成：{result.InputPath} → {result.OutputPath}（去除 {result.Marks.Count} 处水印）",
            ProcessingStatus.NoWatermarkFound => $"未检测到水印：{result.InputPath}",
            _ => $"处理失败：{result.InputPath}（{result.Error}）",
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
