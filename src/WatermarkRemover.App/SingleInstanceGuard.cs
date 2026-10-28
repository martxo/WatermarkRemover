using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace WatermarkRemover.App;

/// <summary>
/// One running 办公助理 per Windows session. A second launch — the usual double-click on the
/// desktop shortcut when the window is behind another app — signals the first process, which
/// restores and foregrounds its window, then exits instead of opening another copy.
/// Multiple copies also raced on the aliyunpan config and produced bogus cloud-update failures,
/// so this guard is the single owner of that config for the session.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "WatermarkRemover.App.SingleInstance";
    private const string ActivateEventName = "WatermarkRemover.App.Activate";
    private const int SwRestore = 9;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly bool _isFirst;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);

        // A normal running copy keeps the mutex for its lifetime, so a double-click waits
        // briefly and then activates it. The wait also covers the update hand-off: the updater
        // may start the new build just before the old process releases the mutex, and exiting
        // immediately there would leave the app closed after an update.
        try
        {
            _isFirst = _mutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            _isFirst = true; // the previous owner crashed; we inherit it
        }

        _activateEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ActivateEventName, out _);
        if (_isFirst)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent, (_, _) => ActivateMainWindow(), null,
                Timeout.Infinite, executeOnlyOnce: false);
        }
        else
        {
            // Wake the running copy; this process shuts down immediately afterwards.
            _activateEvent.Set();
        }
    }

    public bool IsFirst => _isFirst;

    private static void ActivateMainWindow()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Window? window = Application.Current.MainWindow;
            if (window is null) return;

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            // The launching process may not own the foreground (double-clicking while another
            // app is active); restore + SetForegroundWindow is what actually pulls it forward.
            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _wait?.Unregister(null);
        _activateEvent.Dispose();
        if (_isFirst)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* a second instance never owns it */ }
        }

        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
