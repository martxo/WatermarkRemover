using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;
using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.App.Services;

/// <summary>One physical display: origin and size in physical pixels, plus its DPI scale.</summary>
public sealed record MonitorInfo(int Left, int Top, int Width, int Height, double Scale, bool IsPrimary)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public ScreenshotRect Bounds => new(Left, Top, Width, Height);
}

/// <summary>A captured desktop image plus the monitor geometry it was taken from.</summary>
public sealed class DesktopCapture : IDisposable
{
    internal DesktopCapture(SKBitmap bitmap, IReadOnlyList<MonitorInfo> monitors)
    {
        Bitmap = bitmap;
        Monitors = monitors;
        Bounds = new ScreenshotRect(
            monitors.Min(m => m.Left),
            monitors.Min(m => m.Top),
            monitors.Max(m => m.Right) - monitors.Min(m => m.Left),
            monitors.Max(m => m.Bottom) - monitors.Min(m => m.Top));
    }

    public SKBitmap Bitmap { get; }

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Union of every monitor in physical pixels (can start at a negative origin).</summary>
    public ScreenshotRect Bounds { get; }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Screen capture through GDI BitBlt. The process is per-monitor DPI aware (WPF .NET default),
/// so monitor rectangles and the virtual-screen bitmap are physical pixels — the same units the
/// overlay and composer use everywhere.
/// </summary>
public static class ScreenCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int MonitorInfofPrimary = 1;
    private const int MdtEffectiveDpi = 0;
    private const uint SrccopyCaptureblt = 0x00CC0020 | 0x40000000;

    public static DesktopCapture Capture()
    {
        IReadOnlyList<MonitorInfo> monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未检测到显示器");

        var bounds = new ScreenshotRect(
            monitors.Min(m => m.Left),
            monitors.Min(m => m.Top),
            monitors.Max(m => m.Right) - monitors.Min(m => m.Left),
            monitors.Max(m => m.Bottom) - monitors.Min(m => m.Top));
        int width = (int)Math.Round(bounds.Width);
        int height = (int)Math.Round(bounds.Height);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("无法获取屏幕句柄");

        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new Win32Exception("无法创建屏幕位图");

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc,
                    (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), SrccopyCaptureblt))
            {
                throw new Win32Exception("屏幕复制失败（BitBlt）");
            }

            // GetDIBits refuses a bitmap that is still selected into a DC.
            SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;

            // GetDIBits writes straight into the SKBitmap's own pixel buffer, so a full-desktop
            // capture costs one image, not an extra managed copy of it.
            return new DesktopCapture(ReadBitmap(memoryDc, bitmap, width, height), monitors);
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static SKBitmap ReadBitmap(IntPtr memoryDc, IntPtr gdiBitmap, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down rows, matching WPF/Skia
                Planes = 1,
                BitCount = 32,
                Compression = 0, // BI_RGB
            },
        };

        int lines = GetDIBits(memoryDc, gdiBitmap, 0, (uint)height, bitmap.GetPixels(), ref info, 0);
        if (lines != height)
        {
            bitmap.Dispose();
            throw new Win32Exception("读取屏幕像素失败（GetDIBits）");
        }

        // GDI leaves the alpha byte at 0. The SKBitmap is marked Opaque, and the WPF bridge
        // renders opaque bitmaps as Bgr32, so those zero alpha bytes are never displayed.
        return bitmap;
    }

    private static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data)
        {
            var info = new MonitorInfoNative { Size = Marshal.SizeOf<MonitorInfoNative>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                double scale = 1.0;
                try
                {
                    if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
                        scale = dpiX / 96.0;
                }
                catch (Exception)
                {
                    // Shcore is missing on pre-8.1 systems; 100% scaling is the safe assumption.
                }

                monitors.Add(new MonitorInfo(
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top,
                    scale,
                    (info.Flags & MonitorInfofPrimary) != 0));
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            int left = GetSystemMetrics(SmXVirtualScreen);
            int top = GetSystemMetrics(SmYVirtualScreen);
            int width = GetSystemMetrics(SmCxVirtualScreen);
            int height = GetSystemMetrics(SmCyVirtualScreen);
            monitors.Add(new MonitorInfo(left, top, width, height, 1.0, true));
        }

        return monitors;
    }

    // --- Win32 interop ---------------------------------------------------------

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>MONITORINFO (the prefix of MONITORINFOEX): cbSize, monitor, work, flags.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoNative
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoNative info);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines,
        IntPtr bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
