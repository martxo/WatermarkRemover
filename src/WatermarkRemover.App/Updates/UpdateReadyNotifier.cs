using System.ComponentModel;

namespace WatermarkRemover.App.Updates;

/// <summary>What the title-bar update icon is showing right now.</summary>
public enum UpdateVisualState
{
    /// <summary>Nothing to say; the icon is hidden.</summary>
    Idle,

    /// <summary>A download is running in the background, with a percentage when known.</summary>
    Downloading,

    /// <summary>A verified package is on disk, waiting for a restart.</summary>
    Ready,

    /// <summary>The last attempt failed; the icon offers a retry.</summary>
    Failed,
}

/// <summary>
/// The update state the title bar draws and the icon command acts on. It covers the whole
/// background download — start, progress, failure, ready — so a download can never "disappear":
/// if it stalls, dies or is interrupted, the icon says so and offers a retry.
///
/// Publishing happens on the download's context, so every change is marshalled to the UI thread.
/// </summary>
public sealed class UpdateReadyNotifier : INotifyPropertyChanged
{
    /// <summary>One instance per process: the coordinator publishes, the window binds.</summary>
    public static UpdateReadyNotifier Current { get; } = new();

    private string? _version;
    private UpdateVisualState _state = UpdateVisualState.Idle;
    private int? _percent;
    private string? _failure;

    /// <summary>Version string of the update, e.g. "1.16.18".</summary>
    public string? Version => _version;

    /// <summary>Drives the icon's visibility and its look.</summary>
    public UpdateVisualState State => _state;

    /// <summary>Download progress 0–100; null when the size is unknown.</summary>
    public int? Percent => _percent;

    /// <summary>Why the last attempt failed (empty outside the failed state).</summary>
    public string? FailureMessage => _failure;

    /// <summary>True whenever the icon has something to show.</summary>
    public bool HasUpdate => _state != UpdateVisualState.Idle;

    /// <summary>Tooltip: what the icon shows and what clicking it would do.</summary>
    public string Hint => _state switch
    {
        UpdateVisualState.Downloading => _percent is { } percent
            ? $"正在后台下载更新 v{_version}… {percent}%（可继续使用，关闭窗口不影响）"
            : $"正在后台下载更新 v{_version}…（可继续使用，关闭窗口不影响）",
        UpdateVisualState.Ready => $"新版本 v{_version} 已下载，点击重启完成更新",
        UpdateVisualState.Failed => $"更新 v{_version} 下载失败：{_failure}。点击重试",
        _ => string.Empty,
    };

    /// <summary>What a ready icon click runs: hand the verified package to the installer.</summary>
    public Action? Apply { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A download started; the icon begins reporting.</summary>
    internal void BeginDownloading(string version)
    {
        Publish(() =>
        {
            _version = version;
            _state = UpdateVisualState.Downloading;
            _percent = 0;
            _failure = null;
            Apply = null;
            return true;
        });
    }

    /// <summary>Progress tick from the downloader; ignores ticks that arrive after a verdict,
    /// and ticks that do not change the whole percent (the bar updates many times per second).</summary>
    internal void ReportProgress(int? percent)
    {
        Publish(() =>
        {
            if (_state != UpdateVisualState.Downloading || percent == _percent) return false;
            _percent = percent;
            return true;
        });
    }

    /// <summary>
    /// The download finished and verified: the icon turns into the "restart to update" entry.
    /// </summary>
    internal void PublishReady(string version, Action apply)
    {
        Publish(() =>
        {
            _version = version;
            _state = UpdateVisualState.Ready;
            _percent = 100;
            _failure = null;
            Apply = apply;
            return true;
        });
    }

    /// <summary>The attempt failed or was cancelled; the icon offers a retry and keeps the reason.</summary>
    internal void PublishFailed(string version, string reason)
    {
        Publish(() =>
        {
            _version = version;
            _state = UpdateVisualState.Failed;
            _percent = null;
            _failure = string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason;
            Apply = null;
            return true;
        });
    }

    /// <summary>Compatibility alias for a finished download (tests and call sites read naturally).</summary>
    internal void Publish(string version, Action apply) => PublishReady(version, apply);

    /// <summary>Back to idle (an explicit dismiss or a fresh test).</summary>
    internal void Clear()
    {
        Publish(() =>
        {
            _version = null;
            _state = UpdateVisualState.Idle;
            _percent = null;
            _failure = null;
            Apply = null;
            return true;
        });
    }

    private void Publish(Func<bool> change)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        void ApplyChange()
        {
            if (!change()) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FailureMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUpdate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
        }

        if (dispatcher is null || dispatcher.CheckAccess()) ApplyChange();
        else dispatcher.BeginInvoke(ApplyChange);
    }
}
