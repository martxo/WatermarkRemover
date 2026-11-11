using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.VisualBasic.FileIO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// The shared job store plus the per-section view over it: every page keeps its own history, and
/// the status counts and 清除 only ever see records belonging to the section you are on. Owns the
/// list mutations (add / complete / clear / delete / move-to-top) and their notifications, so the
/// shell never touches the collection directly.
/// </summary>
public sealed class JobBoard : ObservableObject
{
    private readonly object _sync = new();
    private readonly IDialogService _dialogs;
    private ICollectionView? _jobsView;
    private int _completedCount;
    private bool _isBusy;
    private bool _scanInProgress;
    private string _sectionId = string.Empty;
    private string _sectionName = string.Empty;

    public JobBoard(IDialogService dialogs)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        ClearCommand = new RelayCommand(Clear, () => VisibleJobCount > 0 && !IsBusy);
        DeleteCommand = new RelayCommand(Delete, p => p is FileJobViewModel && !IsBusy);
    }

    /// <summary>Every record across all sections; the view below filters it.</summary>
    public ObservableCollection<FileJobViewModel> Jobs { get; } = new();

    /// <summary>Records of the current section only — every page shows its own history,
    /// scan runs never surface inside 去水印 and vice versa.</summary>
    public ICollectionView JobsView
    {
        get
        {
            if (_jobsView is null)
            {
                _jobsView = CollectionViewSource.GetDefaultView(Jobs);
                _jobsView.Filter = o => o is FileJobViewModel j && j.OwnerFeatureId == _sectionId;
            }
            return _jobsView;
        }
    }

    public RelayCommand ClearCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>Number of records visible on the current section (status bar + 清除 gate).</summary>
    public int VisibleJobCount => Jobs.Count(j => j.OwnerFeatureId == _sectionId);

    public int CompletedCount
    {
        get => _completedCount;
        private set
        {
            if (SetProperty(ref _completedCount, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// True while the scanner is running. The busy counter ("已完成 0/19") counts batch jobs and
    /// a scan is ONE job that only completes at the end — showing it during a scan read as a
    /// stuck page counter (1.16.133 user report "0/19 是页数吗？怎么不动"). During a scan the
    /// footer hands over to a clear pointer; the live page count lives on the record row.
    /// </summary>
    public bool ScanInProgress
    {
        get => _scanInProgress;
        set
        {
            if (SetProperty(ref _scanInProgress, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => IsBusy && ScanInProgress
        ? "正在扫描…（进度见记录行）"
        : IsBusy
            ? $"处理中… 已完成 {CompletedCount}/{VisibleJobCount}"
            : VisibleJobCount == 0
            ? _sectionId == "transfer.page"
                ? "手机扫码上传后，接收的文件会显示在这里"
                : _sectionId == "screenshot.page"
                    ? "点「开始截图」或按全局快捷键，截图会自动复制并保存成 PNG"
                    : _sectionId == "search.page"
                        ? "输入文件名搜索全盘文件，选中后可直接打开或定位"
                        : _sectionId == "scan.page"
                            ? "扫描的页面会出现在这里；已有的 PDF 也可以直接拖进来编辑"
                            : $"拖入 PDF 文件即可开始「{_sectionName}」"
            : $"已完成 {CompletedCount} 个文件";

    /// <summary>The host's busy state changed — recompose the status line and command gates.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(StatusText));
            ClearCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Swaps the view filter without rebuilding the underlying data.</summary>
    public void SetSection(string id, string displayName)
    {
        _sectionId = id ?? string.Empty;
        _sectionName = displayName ?? string.Empty;
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
        ClearCommand.RaiseCanExecuteChanged();
        _jobsView?.Refresh();
    }

    /// <summary>Appends a record and refreshes the section counters.</summary>
    public void Add(FileJobViewModel job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Jobs.Add(job);
        RefreshCounters();
    }

    /// <summary>Starts a new batch: the completion tally resets with the first files.</summary>
    public void ResetCompleted() => CompletedCount = 0;

    /// <summary>
    /// Re-adds records read back from the task journal at startup. They are appended oldest first
    /// (so the newest record still ends up on top) and counted, so the footer matches the list
    /// instead of claiming zero finished files above a full one.
    /// </summary>
    public void Restore(IReadOnlyList<FileJobViewModel> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        foreach (var job in jobs)
            Jobs.Add(job);

        if (jobs.Count == 0) return;

        CompletedCount = jobs.Count;
        RefreshCounters();
    }

    /// <summary>Drops an in-progress placeholder row without touching any file.</summary>
    public void Remove(FileJobViewModel job)
    {
        if (Jobs.Remove(job)) RefreshCounters();
    }

    /// <summary>One job finished (worker thread): count it and float its row to the top.</summary>
    public void MarkCompleted(FileJobViewModel job)
    {
        lock (_sync) CompletedCount++;
        MoveToTop(job);
    }

    /// <summary>
    /// Moves a finished job to the top of the list so the most recently processed file stays on
    /// top. Safe to call from worker threads — collection changes are marshaled to the UI.
    /// </summary>
    public void MoveToTop(FileJobViewModel job)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Move()
        {
            int index = Jobs.IndexOf(job);
            if (index > 0) Jobs.Move(index, 0);
        }

        if (dispatcher.CheckAccess()) Move();
        else dispatcher.BeginInvoke(Move); // never block a batch worker on the UI thread
    }

    /// <summary>Quick suffixes may have changed: refresh the chips of every existing job.</summary>
    public void RefreshSuffixChips(IReadOnlyList<string> suffixes)
    {
        foreach (var job in Jobs)
            job.RefreshSuffixChips(suffixes);
    }

    /// <summary>Clears the current section's records only — each page owns its own history.
    /// Asks first: these rows are also the only record of where the finished files went, and a
    /// mis-click used to wipe them with no way back.</summary>
    private void Clear()
    {
        if (IsBusy) return;
        if (!_dialogs.Confirm(
                $"清空「{_sectionName}」的 {VisibleJobCount} 条记录？已生成的文件不会被删除。",
                "清空列表"))
            return;

        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (Jobs[i].OwnerFeatureId == _sectionId)
                Jobs.RemoveAt(i);
        }
        CompletedCount = 0;
        RefreshCounters();
    }

    /// <summary>
    /// Removes one finished record after a single confirmation. The generated output file goes to
    /// the Recycle Bin (recoverable); the original input file is never touched.
    /// </summary>
    private void Delete(object? parameter)
    {
        if (parameter is not FileJobViewModel job) return;

        bool hasOutput = job.OutputPath is not null && File.Exists(job.OutputPath);
        // Scans and phone transfers have no original: the generated file IS the only copy, so
        // telling the user "源文件不受影响" there is a plain lie with data-loss consequences.
        bool onlyCopy = hasOutput && string.Equals(
            Path.GetFullPath(job.OutputPath!), Path.GetFullPath(job.InputPath),
            StringComparison.OrdinalIgnoreCase);
        string message = onlyCopy
            ? $"确定要删除「{job.DisplayName}」吗？\n\n这是扫描/接收生成的原件，没有其他副本；\n" +
              "删除后只能从回收站找回。"
            : hasOutput
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
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
        ClearCommand.RaiseCanExecuteChanged();
    }
}
