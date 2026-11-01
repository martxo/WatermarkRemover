using System.Runtime.InteropServices;
using System.Windows.Interop;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Services;

/// <summary>
/// The notification-area icon that keeps 办公助理 alive when the main window is closed:
/// left click restores the window, right click offers 打开 / 截图 / 最近截图 / 退出. Uses the
/// Win32 shell API directly so the app does not need a WinForms dependency.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;
    private const uint NifInfo = 0x0010;
    private const uint NinAdd = 0x00000000;
    private const uint NinModify = 0x00000001;
    private const uint NinDelete = 0x00000002;
    private const int NiifInfo = 0x00000001;

    private const int CallbackMessage = 0x8000 + 120; // WM_APP + 120
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmNull = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint IdiApplication = 32512;

    private const uint MenuOpen = 1;
    private const uint MenuCapture = 2;
    private const uint MenuShelf = 3;
    private const uint MenuExit = 4;

    private readonly IntPtr _hwnd;
    private readonly Action _openWindow;
    private readonly Action _capture;
    private readonly Action _shelf;
    private readonly Action _exit;
    private readonly HwndSource? _source;
    private readonly bool _ownsIcon;
    private readonly IntPtr _icon;
    private NotifyIconData _data;
    private bool _added;

    public TrayIconService(
        IntPtr hwnd, Action openWindow, Action capture, Action shelf, Action exit)
    {
        _hwnd = hwnd;
        _openWindow = openWindow;
        _capture = capture;
        _shelf = shelf;
        _exit = exit;

        (_icon, _ownsIcon) = LoadApplicationIcon();

        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "办公助理 · 截图与最近截图快捷键可在设置中修改",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        if (!ShellNotifyIcon(NinAdd, ref _data))
        {
            AppLog.Warn($"托盘图标创建失败（错误码 {Marshal.GetLastWin32Error()}）");
            return;
        }

        _added = true;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>True once the shell accepted the icon (used by the smoke test / diagnostics).</summary>
    public bool IsAdded => _added;

    /// <summary>One-time hint after the first close, so the user knows the app is still running.</summary>
    public void ShowBackgroundHint()
    {
        if (!_added) return;

        _data.uFlags = NifInfo;
        _data.szInfoTitle = "办公助理仍在后台运行";
        _data.szInfo = "截图快捷键仍然可用。右键托盘图标可打开窗口或退出程序。";
        _data.dwInfoFlags = NiifInfo;
        _data.uVersion = 8000;
        ShellNotifyIcon(NinModify, ref _data);
        _data.uFlags = NifMessage | NifIcon | NifTip;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;

        handled = true;
        int mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
        switch (mouseMessage)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                _openWindow();
                break;
            case WmRButtonUp:
                ShowMenu();
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MfString, MenuOpen, "打开办公助理");
        AppendMenu(menu, MfString, MenuCapture, "截图（快捷键见设置）");
        AppendMenu(menu, MfString, MenuShelf, "最近截图");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, MenuExit, "退出程序");

        GetCursorPos(out Point cursor);
        SetForegroundWindow(_hwnd);
        uint command = TrackPopupMenu(
            menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

        switch (command)
        {
            case MenuOpen: _openWindow(); break;
            case MenuCapture: _capture(); break;
            case MenuShelf: _shelf(); break;
            case MenuExit: _exit(); break;
        }
    }

    /// <summary>Small icon from the running executable; falls back to the stock application icon.</summary>
    private static (IntPtr Icon, bool Owns) LoadApplicationIcon()
    {
        IntPtr large = IntPtr.Zero;
        IntPtr small = IntPtr.Zero;
        string? exe = Environment.ProcessPath;
        if (exe is not null)
        {
            try
            {
                uint extracted = ExtractIconEx(exe, 0, out large, out small, 1);
                if (extracted > 0)
                {
                    IntPtr chosen = small != IntPtr.Zero ? small : large;
                    if (large != IntPtr.Zero && large != chosen) DestroyIcon(large);
                    if (small != IntPtr.Zero && small != chosen) DestroyIcon(small);
                    if (chosen != IntPtr.Zero) return (chosen, true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Trace("读取托盘图标失败，使用默认图标", ex);
            }
        }

        return (LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication)), false);
    }

    public void Dispose()
    {
        if (_added)
        {
            ShellNotifyIcon(NinDelete, ref _data);
            _added = false;
        }

        _source?.RemoveHook(WndProc);
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    // --- Win32 interop ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) =>
        Shell_NotifyIcon(message, ref data);
}
