using System.ComponentModel;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// The "an update is downloaded and waiting" state, shared between the background download and
/// the shell icon that announces it. Publishing happens on the download's worker context, so the
/// notification is marshalled to the UI thread — a bound icon must never be touched from there.
/// </summary>
public sealed class UpdateReadyNotifier : INotifyPropertyChanged
{
    /// <summary>One instance per process: the coordinator publishes, the window binds.</summary>
    public static UpdateReadyNotifier Current { get; } = new();

    private string? _version;

    /// <summary>Version string of the waiting update, e.g. "1.16.18".</summary>
    public string? Version => _version;

    /// <summary>Drives the icon's visibility.</summary>
    public bool HasUpdate => _version is not null;

    /// <summary>Tooltip: what the icon would do right now.</summary>
    public string Hint => _version is { } version
        ? $"新版本 v{version} 已下载，点击重启完成更新"
        : string.Empty;

    /// <summary>What the icon click runs: hand the verified package to the installer.</summary>
    public Action? Apply { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Announces a downloaded update; called by the coordinator once verification passed.</summary>
    internal void Publish(string version, Action apply)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        void Set()
        {
            _version = version;
            Apply = apply;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUpdate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
        }

        if (dispatcher is null || dispatcher.CheckAccess()) Set();
        else dispatcher.BeginInvoke(Set);
    }

    /// <summary>Forgets the waiting update (tests and a cancelled hand-over).</summary>
    internal void Clear()
    {
        _version = null;
        Apply = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUpdate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
    }
}
