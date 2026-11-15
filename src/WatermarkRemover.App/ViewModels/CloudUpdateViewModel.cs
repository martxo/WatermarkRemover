using System.Collections.ObjectModel;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One retained mirror version in the list (read-only: the mirror keeps no rollback).</summary>
public sealed class CloudVersionRowViewModel
{
    public CloudVersionRowViewModel(CloudVersionSummary summary, bool isCurrent)
    {
        Version = summary.Version;
        Note = string.IsNullOrWhiteSpace(summary.Note) ? "（无发布说明）" : summary.Note;
        DateText = DateTimeOffset.TryParse(summary.PublishedAt, out DateTimeOffset at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : string.Empty;
        SizeText = MirrorUpdateService.FormatBytes(summary.TotalBytes);
        ChangeText = $"变更 {summary.ChangedFiles} 个文件 · 本版上传 {MirrorUpdateService.FormatBytes(summary.NewBytes)}";
        IsCurrent = isCurrent;
    }

    public string Version { get; }

    public string Note { get; }

    public string DateText { get; }

    public string SizeText { get; }

    public string ChangeText { get; }

    public bool IsCurrent { get; }

    public string VersionText => $"v{Version}";
}

/// <summary>One local install/rollback record.</summary>
public sealed class CloudHistoryRowViewModel
{
    public CloudHistoryRowViewModel(CloudUpdateHistoryEntry entry)
    {
        At = DateTimeOffset.TryParse(entry.At, out DateTimeOffset at)
            ? at.ToLocalTime().ToString("MM-dd HH:mm")
            : entry.At;
        DirectionText = entry.Direction == "rollback" ? "回滚" : "更新";
        VersionText = string.IsNullOrEmpty(entry.FromVersion)
            ? $"v{entry.ToVersion}"
            : $"v{entry.FromVersion} → v{entry.ToVersion}";
        ChangeText = $"{entry.FileCount} 个文件 · {MirrorUpdateService.FormatBytes(entry.Bytes)}";
        StatusText = string.IsNullOrWhiteSpace(entry.Message)
            ? entry.Status
            : $"{entry.Status}：{entry.Message}";
        IsFailure = entry.Status.Contains("还原", StringComparison.Ordinal) ||
                    entry.Status.Contains("失败", StringComparison.Ordinal);
    }

    public string At { get; }

    public string DirectionText { get; }

    public string VersionText { get; }

    public string ChangeText { get; }

    public string StatusText { get; }

    public bool IsFailure { get; }
}

/// <summary>
/// The 云更新中心: checks the 腾讯云镜像 repository, shows what a release actually changes and
/// lists the retained versions. No rollback and no netdisk login — the mirror is a one-way
/// incremental channel that keeps only the newest few versions.
/// </summary>
public sealed class CloudUpdateViewModel : ObservableObject
{
    private readonly MirrorUpdateService _service;
    private readonly IDialogService _dialogs;
    private CloudCheckResult? _check;
    private bool _busy;
    private string _statusText = "正在准备…";
    private string _detailText = string.Empty;
    private double _progressPercent;
    private bool _progressIndeterminate = true;
    private string _progressText = string.Empty;

    public CloudUpdateViewModel(AppSettings settings, IDialogService dialogs)
    {
        _service = new MirrorUpdateService(settings);
        _dialogs = dialogs;

        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsBusy);
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => !IsBusy && HasUpdate);
        RefreshHistoryCommand = new RelayCommand(ReloadHistory);
    }

    public string CurrentVersionText => $"当前版本 v{UpdateService.CurrentVersion}";

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CheckCommand.RaiseCanExecuteChanged();
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsBusy;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetProperty(ref _progressIndeterminate, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool HasUpdate { get; private set; }

    public string ApplyButtonText { get; private set; } = "立即更新";

    public ObservableCollection<CloudVersionRowViewModel> Versions { get; } = new();

    public ObservableCollection<CloudHistoryRowViewModel> History { get; } = new();

    public RelayCommand CheckCommand { get; }

    public RelayCommand ApplyCommand { get; }

    public RelayCommand RefreshHistoryCommand { get; }

    public async Task InitializeAsync()
    {
        ReloadHistory();
        await CheckAsync().ConfigureAwait(true);
    }

    private async Task CheckAsync()
    {
        IsBusy = true;
        HasUpdate = false;
        ApplyButtonText = "立即更新";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ApplyButtonText));
        ProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = string.Empty;
        StatusText = "正在检查镜像版本…";
        DetailText = string.Empty;

        try
        {
            var result = await _service.CheckAsync(UpdateService.CurrentVersion).ConfigureAwait(true);
            ShowCheck(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowCheck(CloudCheckResult result)
    {
        _check = result;
        StatusText = result.Message;
        DetailText = result.Plan is null ? string.Empty : string.Join(Environment.NewLine, result.Plan.Describe());

        HasUpdate = result.UpdateAvailable && result.Plan is { HasChanges: true };
        ApplyButtonText = HasUpdate && result.Plan is not null && result.Latest is not null
            ? $"更新到 v{result.Latest}（仅下载 {MirrorUpdateService.FormatBytes(result.Plan.DownloadBytes)}）"
            : "已是最新";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ApplyButtonText));
        ApplyCommand.RaiseCanExecuteChanged();

        Versions.Clear();
        if (result.Versions is not null)
        {
            foreach (CloudVersionSummary summary in result.Versions.Versions)
            {
                bool isCurrent = string.Equals(summary.Version, result.Current.ToString(), StringComparison.OrdinalIgnoreCase);
                Versions.Add(new CloudVersionRowViewModel(summary, isCurrent));
            }
        }
    }

    private async Task ApplyAsync()
    {
        if (_check is not { } check || check.Plan is null || check.Latest is null) return;
        if (!_dialogs.ConfirmYesNo(
                $"镜像发现新版本 v{check.Latest}（当前 v{check.Current}）。\n\n" +
                $"需要下载 {check.Plan.ChangedFiles.Count} 个文件 / {MirrorUpdateService.FormatBytes(check.Plan.DownloadBytes)}" +
                $"（只取变更的分块，不再下载几百兆的完整包）。\n\n" +
                "更新会短暂关闭程序，替换文件后自动重新打开。是否继续？",
                "云更新"))
        {
            return;
        }

        await StageAndApplyAsync(check, "update", $"更新到 v{check.Latest}").ConfigureAwait(true);
    }

    private async Task StageAndApplyAsync(CloudCheckResult check, string direction, string note)
    {
        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressPercent = 0;
        StatusText = "正在下载变更分块…";

        try
        {
            var progress = new Progress<CloudUpdateProgress>(p =>
            {
                ProgressIndeterminate = p.Percent is null;
                if (p.Percent is double percent) ProgressPercent = percent;
                ProgressText = p.Stage switch
                {
                    CloudUpdateStage.Downloading when p.TotalBytes is > 0 =>
                        $"{MirrorUpdateService.FormatBytes(p.Bytes)} / {MirrorUpdateService.FormatBytes(p.TotalBytes.Value)}",
                    CloudUpdateStage.Assembling => p.Message ?? "正在组装文件…",
                    _ => p.Message ?? string.Empty,
                };
                if (!string.IsNullOrEmpty(p.Message)) StatusText = p.Message;
            });

            CloudStageResult staged = await _service
                .StageAsync(check, progress, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrEmpty(staged.StagingDirectory))
            {
                StatusText = "没有需要下载的文件。";
                return;
            }

            StatusText = "下载完成，正在重启并替换文件…";
            App.NoteExitReason($"云更新（{direction} {note}），重启以替换文件");
            _service.ApplyAndRestart(staged, direction, note);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Error($"云更新失败（{direction} {note}）", ex);
            StatusText = $"更新失败：{ex.Message}";
            _dialogs.Warn(
                $"云更新失败，当前版本不受影响。\n\n原因：{ex.Message}\n\n" +
                "可稍后重试；如镜像不可用，可在「软件更新」里使用 GitHub 全量安装包作为兜底。",
                "云更新");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReloadHistory()
    {
        History.Clear();
        foreach (CloudUpdateHistoryEntry entry in MirrorUpdateService.ReadHistory().Reverse())
        {
            History.Add(new CloudHistoryRowViewModel(entry));
        }
    }
}
