using System.Collections.ObjectModel;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One retained cloud version in the list, with a rollback entry point.</summary>
public sealed class CloudVersionRowViewModel
{
    public CloudVersionRowViewModel(CloudVersionSummary summary, bool isCurrent, Action<string> rollback)
    {
        Version = summary.Version;
        Note = string.IsNullOrWhiteSpace(summary.Note) ? "（无发布说明）" : summary.Note;
        DateText = DateTimeOffset.TryParse(summary.PublishedAt, out DateTimeOffset at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : string.Empty;
        SizeText = CloudUpdateService.FormatBytes(summary.TotalBytes);
        ChangeText = $"变更 {summary.ChangedFiles} 个文件 · 本版上传 {CloudUpdateService.FormatBytes(summary.NewBytes)}";
        IsCurrent = isCurrent;
        RollbackCommand = new RelayCommand(
            () => rollback(Version),
            () => !isCurrent);
    }

    public string Version { get; }

    public string Note { get; }

    public string DateText { get; }

    public string SizeText { get; }

    public string ChangeText { get; }

    public bool IsCurrent { get; }

    public string VersionText => $"v{Version}";

    public RelayCommand RollbackCommand { get; }
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
        ChangeText = $"{entry.FileCount} 个文件 · {CloudUpdateService.FormatBytes(entry.Bytes)}";
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
/// The 云更新中心: checks the 阿里云盘 repository, shows what a release actually changes, lists the
/// retained versions with one-click rollback and keeps the local install/rollback history.
/// </summary>
public sealed class CloudUpdateViewModel : ObservableObject
{
    private readonly CloudUpdateService _service;
    private readonly IDialogService _dialogs;
    private readonly Action _openNetdiskSettings;
    private CloudCheckResult? _check;
    private bool _busy;
    private bool _needsLogin;
    private string _statusText = "正在准备…";
    private string _detailText = string.Empty;
    private double _progressPercent;
    private bool _progressIndeterminate = true;
    private string _progressText = string.Empty;

    public CloudUpdateViewModel(AppSettings settings, IDialogService dialogs, Action openNetdiskSettings)
    {
        _service = new CloudUpdateService(settings, new DpapiSecretProtector());
        _dialogs = dialogs;
        _openNetdiskSettings = openNetdiskSettings;

        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsBusy);
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => !IsBusy && HasUpdate);
        OpenLoginCommand = new RelayCommand(openNetdiskSettings, () => !IsBusy && NeedsLogin);
        RefreshHistoryCommand = new RelayCommand(ReloadHistory);
    }

    public string CurrentVersionText => $"当前版本 v{UpdateService.CurrentVersion}";

    public string AccountText => !_service.IsConfigured
        ? "阿里云盘：未登录。每台电脑首次使用请扫码登录一次。"
        : _needsLogin
            ? $"阿里云盘：登录已失效（{_service.AccountName}），请重新扫码登录。"
            : $"阿里云盘账号：{_service.AccountName}（资源库 / WatermarkRemover）";

    public bool NeedsLogin => _needsLogin || !_service.IsConfigured;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CheckCommand.RaiseCanExecuteChanged();
            ApplyCommand.RaiseCanExecuteChanged();
            OpenLoginCommand.RaiseCanExecuteChanged();
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

    public RelayCommand OpenLoginCommand { get; }

    public RelayCommand RefreshHistoryCommand { get; }

    public async Task InitializeAsync()
    {
        ReloadHistory();
        await CheckAsync().ConfigureAwait(true);
    }

    private async Task CheckAsync()
    {
        IsBusy = true;
        _needsLogin = !_service.IsConfigured;
        HasUpdate = false;
        ApplyButtonText = "立即更新";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ApplyButtonText));
        OnPropertyChanged(nameof(NeedsLogin));
        OnPropertyChanged(nameof(AccountText));
        ProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = string.Empty;
        StatusText = "正在检查云端版本…";
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
        _needsLogin = !_service.IsConfigured ||
                      (result.Status == CloudCheckStatus.Failed && LooksLikeAuthFailure(result.Message));
        StatusText = result.Message;
        DetailText = result.Plan is null ? string.Empty : string.Join(Environment.NewLine, result.Plan.Describe());

        HasUpdate = result.UpdateAvailable && result.Plan is { HasChanges: true };
        ApplyButtonText = HasUpdate && result.Plan is not null && result.Latest is not null
            ? $"更新到 v{result.Latest}（仅下载 {CloudUpdateService.FormatBytes(result.Plan.DownloadBytes)}）"
            : "已是最新";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ApplyButtonText));
        OnPropertyChanged(nameof(NeedsLogin));
        OnPropertyChanged(nameof(AccountText));
        ApplyCommand.RaiseCanExecuteChanged();
        OpenLoginCommand.RaiseCanExecuteChanged();

        Versions.Clear();
        if (result.Versions is not null)
        {
            foreach (CloudVersionSummary summary in result.Versions.Versions)
            {
                bool isCurrent = string.Equals(summary.Version, result.Current.ToString(), StringComparison.OrdinalIgnoreCase);
                Versions.Add(new CloudVersionRowViewModel(summary, isCurrent, version => _ = RollbackAsync(version)));
            }
        }
    }

    private async Task ApplyAsync()
    {
        if (_check is not { } check || check.Plan is null || check.Latest is null) return;
        if (!_dialogs.ConfirmYesNo(
                $"云端发现新版本 v{check.Latest}（当前 v{check.Current}）。\n\n" +
                $"需要下载 {check.Plan.ChangedFiles.Count} 个文件 / {CloudUpdateService.FormatBytes(check.Plan.DownloadBytes)}" +
                $"（只取变更的分块，不再下载几百兆的完整包）。\n\n" +
                "更新会短暂关闭程序，替换文件后自动重新打开。是否继续？",
                "云更新"))
        {
            return;
        }

        await StageAndApplyAsync(check, "update", $"更新到 v{check.Latest}").ConfigureAwait(true);
    }

    private async Task RollbackAsync(string version)
    {
        if (!_dialogs.ConfirmYesNo(
                $"确定回滚到 v{version} 吗？\n\n" +
                "回滚只会下载与该版本不同的文件分块，完成后程序自动重启；仍可再更新回最新版。",
                "云更新回滚"))
        {
            return;
        }

        IsBusy = true;
        StatusText = $"正在准备回滚到 v{version}…";
        try
        {
            CloudCheckResult check = await _service
                .CheckVersionAsync(UpdateService.CurrentVersion, version)
                .ConfigureAwait(true);
            if (!check.UpdateAvailable || check.Plan is null)
            {
                ShowCheck(check);
                _dialogs.Warn(check.Message, "云更新回滚");
                return;
            }

            IsBusy = false;
            await StageAndApplyAsync(check, "rollback", $"回滚到 v{version}").ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
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
                        $"{CloudUpdateService.FormatBytes(p.Bytes)} / {CloudUpdateService.FormatBytes(p.TotalBytes.Value)}",
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
            AppLog.Flush();
            _service.ApplyAndRestart(staged, direction, note);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Error($"云更新失败（{direction} {note}）", ex);
            StatusText = $"更新失败：{ex.Message}";
            _dialogs.Warn(
                $"云更新失败，当前版本不受影响。\n\n原因：{ex.Message}\n\n" +
                "可稍后重试；如云端不可用，可在「软件更新」里使用 GitHub 全量安装包作为兜底。",
                "云更新");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Auth-shaped failures get the login button instead of a silent GitHub fallback.</summary>
    private static bool LooksLikeAuthFailure(string message) =>
        message.Contains("令牌", StringComparison.Ordinal) ||
        message.Contains("授权", StringComparison.Ordinal) ||
        message.Contains("登录", StringComparison.Ordinal) ||
        message.Contains("AccessToken", StringComparison.OrdinalIgnoreCase);

    private void ReloadHistory()
    {
        History.Clear();
        foreach (CloudUpdateHistoryEntry entry in CloudUpdateService.ReadHistory().Reverse())
        {
            History.Add(new CloudHistoryRowViewModel(entry));
        }
    }
}
