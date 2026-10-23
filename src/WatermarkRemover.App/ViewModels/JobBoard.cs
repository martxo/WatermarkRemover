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

    public string StatusText => IsBusy
        ? $"处理中… 已完成 {CompletedCount}/{VisibleJobCount}"
        : VisibleJobCount == 0
            ? _sectionId == "transfer.page"
                ? "手机扫码上传后，接收的文件会显示在这里"
                : _sectionId == "search.page"
                    ? "输入文件名搜索全盘文件，选中后可直接打开或定位"
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
        else dispatcher.Invoke(Move);
    }

    /// <summary>Quick suffixes may have changed: refresh the chips of every existing job.</summary>
    public void RefreshSuffixChips(IReadOnlyList<string> suffixes)
    {
        foreach (var job in Jobs)
            job.RefreshSuffixChips(suffixes);
    }

    /// <summary>Clears the current section's records only — each page owns its own history.</summary>
    private void Clear()
    {
        if (IsBusy) return;
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
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(VisibleJobCount));
        OnPropertyChanged(nameof(StatusText));
        ClearCommand.RaiseCanExecuteChanged();
    }
}
