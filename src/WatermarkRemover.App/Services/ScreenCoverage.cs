using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 把无边框看图窗口铺满目标显示器的**完整物理边界**（含任务栏那条）。
/// WPF 的 Width/Height 是 DIP，再叠加 WindowStyle=None + AllowsTransparency 的铺屏怪癖，
/// 换算稍一偏差窗口就比屏幕矮一截——页面按这个更矮的视口「适应窗口」，下方内容就出了视野。
/// 这里直接调 Win32 的 SetWindowPos，用显示器的物理像素矩形设窗口，绕开所有 DIP 换算。
/// </summary>
internal static class ScreenCoverage
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>铺满主窗口所在的那块屏；主窗口不可用时退回窗口自己所在的屏。</summary>
    internal static void CoverScreenOf(Window window)
    {
        IntPtr windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero) return;

        // 多屏时跟着主窗口走：看图铺在主窗口那块屏上，而不是永远铺主屏。
        IntPtr target = windowHandle;
        var main = Application.Current?.MainWindow;
        if (main is not null && main.IsVisible)
        {
            IntPtr mainHandle = new WindowInteropHelper(main).Handle;
            if (mainHandle != IntPtr.Zero) target = mainHandle;
        }

        IntPtr monitor = MonitorFromWindow(target, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        int width = info.Monitor.Right - info.Monitor.Left;
        int height = info.Monitor.Bottom - info.Monitor.Top;

        window.WindowState = WindowState.Normal;
        bool covered = SetWindowPos(
            windowHandle,
            window.Topmost ? HwndTopmost : IntPtr.Zero,
            info.Monitor.Left, info.Monitor.Top, width, height,
            SwpNoActivate | SwpShowWindow);

        if (covered)
        {
            AppLog.Info($"看图窗口铺屏：显示器 {width}×{height} @{info.Monitor.Left},{info.Monitor.Top}，" +
                        $"Topmost={window.Topmost}，SetWindowPos=ok");
        }
        else
        {
            // SetWindowPos 失败时退回 WPF 尺寸：按当前 DPI 把物理矩形折算成 DIP。
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            window.Width = width / dpi.DpiScaleX;
            window.Height = height / dpi.DpiScaleY;
            AppLog.Warn($"看图窗口铺屏：SetWindowPos 失败，改用 DIP {window.Width:0}×{window.Height:0}");
        }

        window.Dispatcher.BeginInvoke(() => AppLog.Info(
            $"看图窗口实际：{window.ActualWidth:0}×{window.ActualHeight:0} DIP，" +
            $"位置 {window.Left:0},{window.Top:0}，状态 {window.WindowState}"));
    }
}
