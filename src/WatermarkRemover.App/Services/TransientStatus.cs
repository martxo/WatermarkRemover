namespace WatermarkRemover.App.Services;

/// <summary>
/// One transient message slot shared by every surface that says "done" for a moment (strip
/// rotate/delete/straighten, a numbered rename on the row). The rule is the same everywhere:
/// show the line, clear it after a few seconds, and never let an older timer wipe newer text.
/// </summary>
public sealed class TransientStatus
{
    private readonly Func<string?> _read;
    private readonly Action<string?> _write;
    private readonly TimeSpan _hold;
    private int _token;

    /// <param name="read">Current text of the target slot — the timer only clears its own message.</param>
    /// <param name="write">Writes the target slot; null clears it.</param>
    /// <param name="hold">How long a message stays before it expires (default 3 seconds).</param>
    public TransientStatus(Func<string?> read, Action<string?> write, TimeSpan? hold = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _hold = hold ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Shows a message and schedules its expiry; a newer message supersedes this one.</summary>
    public void Show(string message)
    {
        _write(message);
        int token = ++_token;

        // No dispatcher (unit tests, headless hosts): the line stays — nothing can tick it away.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _ = Task.Delay(_hold).ContinueWith(_ =>
            dispatcher.BeginInvoke(() =>
            {
                if (token != _token) return; // a newer message owns the line
                if (!string.Equals(_read(), message, StringComparison.Ordinal)) return;
                _write(null);
            }));
    }
}
