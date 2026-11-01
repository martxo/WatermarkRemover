using System.Runtime.InteropServices;
using System.Text;
using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Finds the top-level application window under a screen point. Our overlay covers the whole
/// desktop, so WindowFromPoint would always return the overlay itself; instead this walks
/// EnumWindows' Z-order and skips this process, the shell, tool windows and cloaked UWP frames.
/// The visible frame (DWM extended bounds) is used so selections match what the user sees.
/// </summary>
public static class ScreenshotWindowDetector
{
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTransparent = 0x00000020;
    private const int GwlExStyle = -20;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private static readonly string[] ShellClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
    };

    /// <summary>The visible window frame containing the point, or null when only the desktop is there.</summary>
    public static ScreenshotRect? FindAt(double x, double y)
    {
        ScreenshotRect? found = null;
        int ownProcess = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out int processId);
            if (processId == ownProcess) return true;

            IntPtr style = GetWindowLongPtr(hwnd, GwlExStyle);
            long exStyle = style.ToInt64();
            if ((exStyle & WsExToolWindow) != 0 || (exStyle & WsExTransparent) != 0) return true;

            if (IsCloaked(hwnd)) return true;

            var className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) > 0 &&
                Array.Exists(ShellClasses, c => className.ToString().Equals(c, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!TryGetVisibleFrame(hwnd, out Rect rect)) return true;
            if (rect.Right - rect.Left < 40 || rect.Bottom - rect.Top < 30) return true;
            if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom) return true;

            found = ScreenshotRect.FromPoints(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return false; // first hit in Z-order wins
        }, IntPtr.Zero);

        return found;
    }

    private static bool TryGetVisibleFrame(IntPtr hwnd, out Rect rect)
    {
        if (DwmGetWindowAttribute(
                hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Rect>()) == 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, out int value, int size);
}
