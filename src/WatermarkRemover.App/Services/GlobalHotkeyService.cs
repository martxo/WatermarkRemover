using System.Runtime.InteropServices;
using System.Windows.Interop;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Registers one system-wide shortcut on the main window's message loop. The callback runs on
/// the UI thread, so the shell can start the screenshot overlay directly from WM_HOTKEY.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5348; // "SH"
    /// <summary>Id used only for the settings-page conflict probe; never left registered.</summary>
    private const int ProbeHotkeyId = 0x5349;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private readonly IntPtr _hwnd;
    private readonly Action _onPressed;
    private readonly HwndSource? _source;
    private bool _registered;

    public GlobalHotkeyService(IntPtr hwnd, Action onPressed)
    {
        _hwnd = hwnd;
        _onPressed = onPressed ?? throw new ArgumentNullException(nameof(onPressed));
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>Points the global shortcut at <paramref name="hotkey"/>; false keeps the old one off.</summary>
    public bool Register(ScreenshotHotkey hotkey, out string? error)
    {
        error = null;
        Unregister();

        if (!TryMap(hotkey, out uint modifiers, out uint virtualKey, out error)) return false;

        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers, virtualKey);
        if (!_registered)
        {
            error = DescribeFailure(Marshal.GetLastWin32Error());
            AppLog.Warn($"注册截图快捷键 {hotkey} 失败：{error}");
        }
        else
        {
            AppLog.Info($"已注册截图快捷键 {hotkey}");
        }

        return _registered;
    }

    /// <summary>
    /// Tests a gesture without touching the live shortcut, so the settings page can reject a
    /// conflicting combination BEFORE saving it (and before the old shortcut is given up).
    /// </summary>
    public bool CanRegister(ScreenshotHotkey hotkey, out string? error)
    {
        error = null;
        if (!TryMap(hotkey, out uint modifiers, out uint virtualKey, out error)) return false;

        bool ok = RegisterHotKey(_hwnd, ProbeHotkeyId, modifiers, virtualKey);
        if (ok)
        {
            UnregisterHotKey(_hwnd, ProbeHotkeyId);
            return true;
        }

        error = DescribeFailure(Marshal.GetLastWin32Error());
        return false;
    }

    private static bool TryMap(ScreenshotHotkey hotkey, out uint modifiers, out uint virtualKey, out string? error)
    {
        modifiers = 0;
        error = null;

        virtualKey = VirtualKeyFor(hotkey.Key);
        if (virtualKey == 0)
        {
            error = $"不支持的按键「{hotkey.Key}」";
            return false;
        }

        if (hotkey.Control) modifiers |= ModControl;
        if (hotkey.Alt) modifiers |= ModAlt;
        if (hotkey.Shift) modifiers |= ModShift;
        if (hotkey.Windows) modifiers |= ModWin;
        return true;
    }

    private static string DescribeFailure(int code) => code == ErrorHotkeyAlreadyRegistered
        ? "该组合键已被其他程序占用"
        : $"注册失败（错误码 {code}）";

    private void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

        handled = true;
        try
        {
            _onPressed();
        }
        catch (Exception ex)
        {
            AppLog.Error("全局快捷键回调失败", ex);
        }

        return IntPtr.Zero;
    }

    /// <summary>VK code for the key names <see cref="ScreenshotHotkey"/> accepts.</summary>
    private static uint VirtualKeyFor(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        if (key.Length == 1)
        {
            char upper = char.ToUpperInvariant(key[0]);
            return upper is >= 'A' and <= 'Z' or >= '0' and <= '9' ? (uint)upper : 0;
        }

        if (key == "PrintScreen") return 0x2C; // VK_SNAPSHOT

        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int number) && number is >= 1 and <= 24)
            return (uint)(0x70 + number - 1); // VK_F1 = 0x70

        return 0;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
