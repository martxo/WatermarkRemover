using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Every clipboard write in the app goes through here.
///
/// Why not WPF's <see cref="Clipboard"/>: when another process holds the clipboard open,
/// <c>Clipboard.SetText</c> burns ~100 ms inside WPF's own internal retry loop and then throws
/// CLIPBRD_E_CANT_OPEN (0x800401D0) — even when the text had already reached the clipboard and
/// only the flush step failed. Callers then reported 复制失败 for a copy that had actually
/// worked, while the UI thread was frozen for the whole retry. Two earlier attempts could not
/// fix that: a retry loop merely multiplies WPF's internal wait, and a read-back check is itself
/// an OpenClipboard call, so it fails for exactly the same reason while the lock is held.
///
/// This writes through raw Win32 instead, where every step is observable:
/// OpenClipboard (bounded retry) → EmptyClipboard → SetClipboardData. When SetClipboardData
/// returns non-NULL the text is on the clipboard — no later step can turn that into a failure.
/// </summary>
public static class AppClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Blocking budget while the clipboard is locked elsewhere: ~120 ms, then give up.</summary>
    private const int OpenAttempts = 6;
    private const int OpenRetryDelayMs = 20;

    private static IntPtr _ownerWindow;

    /// <summary>Copies text. Reports success exactly when the text reached the clipboard.</summary>
    public static void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        RequireStaThread();

        for (int attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            if (TryWriteText(text)) return;
            if (attempt < OpenAttempts) Thread.Sleep(OpenRetryDelayMs);
        }

        // The lock was never ours in this call, but a previous write may already have landed.
        if (HoldsText(text)) return;

        AppLog.Warn("写入剪贴板失败：OpenClipboard 重试 " + OpenAttempts + " 次后仍被占用");
        throw new ExternalException(
            "剪贴板正被其他程序占用，请稍后再试。", unchecked((int)0x800401D0));
    }

    /// <summary>
    /// Copies an image. WPF's path retries internally and is kept as-is; the only addition is
    /// the read-back verdict, so an image that landed despite a failed flush is not reported
    /// as a failed copy.
    /// </summary>
    public static void SetImage(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequireStaThread();

        try
        {
            Clipboard.SetImage(image);
        }
        catch (ExternalException)
        {
            if (!HoldsImage(image)) throw;
        }
    }

    /// <summary>
    /// One full write attempt. Returns true the moment SetClipboardData accepted the data —
    /// that is the definition of success here, with no flush step left to fail afterwards.
    /// </summary>
    private static bool TryWriteText(string text)
    {
        if (!OpenClipboard(OwnerWindow)) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * sizeof(char)));
            if (memory == IntPtr.Zero)
            {
                throw new OutOfMemoryException("分配剪贴板内存失败。");
            }

            IntPtr locked = GlobalLock(memory);
            if (locked == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new OutOfMemoryException("锁定剪贴板内存失败。");
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                Marshal.WriteInt16(locked, text.Length * sizeof(char), 0); // terminating NUL
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CF_UNICODETEXT, memory) != IntPtr.Zero) return true;

            // Ownership was not transferred, so the block is still ours to free.
            GlobalFree(memory);
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// The clipboard owner: the main window. A clipboard owned by nobody is wiped when the
    /// owning task exits, so the text must be tied to a window that outlives the copy click.
    /// </summary>
    private static IntPtr OwnerWindow
    {
        get
        {
            if (_ownerWindow != IntPtr.Zero) return _ownerWindow;
            try
            {
                if (Application.Current?.MainWindow is Window window)
                {
                    _ownerWindow = new WindowInteropHelper(window).Handle;
                }
            }
            catch (Exception)
            {
                // No window available — a NULL owner is still legal for OpenClipboard.
            }

            return _ownerWindow;
        }
    }

    private static void RequireStaThread()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new ThreadStateException("写入剪贴板必须在 UI 线程调用。");
        }
    }

    /// <summary>
    /// Read-back check: is our text already on the clipboard? Only consulted when the lock
    /// could never be acquired — a previous write can leave the text there even when its own
    /// flush failed. A verification step must never be the thing that throws.
    /// </summary>
    private static bool HoldsText(string text)
    {
        try
        {
            return Clipboard.ContainsText() &&
                   string.Equals(Clipboard.GetText(), text, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read-back check for images; the shape is enough to recognise our own write.</summary>
    private static bool HoldsImage(BitmapSource image)
    {
        try
        {
            return Clipboard.GetImage() is { } present &&
                   present.PixelWidth == image.PixelWidth &&
                   present.PixelHeight == image.PixelHeight;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
