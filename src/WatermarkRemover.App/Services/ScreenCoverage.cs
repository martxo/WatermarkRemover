using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 把无边框看图窗口铺满目标显示器的**完整物理边界**（含任务栏那条）。
/// 只用 <c>WindowStyle=None + AllowsTransparency=True + WindowState=Maximized</c> 时，窗口会停在
/// 工作区（任务栏之外）——底部露出一条桌面，页面也因此按更矮的高度「适应窗口」，看起来下方被裁。
/// 这里直接读显示器矩形，再按窗口 DPI 换算成 DIP 后显式设置 Left/Top/Width/Height。
/// </summary>
internal static class ScreenCoverage
{
    private const uint MonitorDefaultToNearest = 2;

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

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        // 显示器矩形是物理像素，窗口尺寸是 DIP：按当前 DPI 换算，否则高缩放下会短一截。
        window.WindowState = WindowState.Normal;
        window.Left = info.Monitor.Left / dpi.DpiScaleX;
        window.Top = info.Monitor.Top / dpi.DpiScaleY;
        window.Width = (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX;
        window.Height = (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY;
    }
}
