using System.Globalization;

namespace WatermarkRemover.Core.Features.Screenshot;

/// <summary>
/// One global shortcut as the settings file stores it: modifiers plus a key, e.g. <c>Ctrl+Alt+A</c>.
/// Parsing lives in Core so the settings page, the hotkey registrar and the tests all agree on
/// what a valid gesture is; nothing here knows about Win32.
/// </summary>
public readonly record struct ScreenshotHotkey(bool Control, bool Alt, bool Shift, bool Windows, string Key)
{
    /// <summary>The gesture every new installation starts with (the Snipaste/PixPin habit).</summary>
    public static readonly ScreenshotHotkey Default = new(true, true, false, false, "A");

    public bool HasModifier => Control || Alt || Shift || Windows;

    /// <summary>Canonical, stable text form saved to settings and shown in the UI.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Windows) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>The stored text, or <see cref="Default"/> when it is missing or malformed.</summary>
    public static ScreenshotHotkey ParseOrDefault(string? text) =>
        TryParse(text, out ScreenshotHotkey hotkey) ? hotkey : Default;

    /// <summary>
    /// Accepts case-insensitive modifier names (Ctrl/Control, Alt, Shift, Win/Windows) and
    /// A-Z, 0-9 or F1-F24 keys. At least one modifier is required: a bare key would be
    /// registered system-wide and steal typing from every application.
    /// </summary>
    public static bool TryParse(string? text, out ScreenshotHotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool control = false, alt = false, shift = false, windows = false;
        string? key = null;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    control = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win":
                case "windows":
                    windows = true;
                    break;
                default:
                    if (key is not null) return false;
                    key = NormalizeKey(raw);
                    if (key is null) return false;
                    break;
            }
        }

        if (key is null || !(control || alt || shift || windows)) return false;

        hotkey = new ScreenshotHotkey(control, alt, shift, windows, key);
        return true;
    }

    /// <summary>Canonical key name, or null when this key cannot be registered reliably.</summary>
    private static string? NormalizeKey(string raw)
    {
        string key = raw.Trim().ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return key;

        if (key.Length is >= 2 and <= 3 && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= 24)
        {
            return "F" + number.ToString(CultureInfo.InvariantCulture);
        }

        return key switch
        {
            "PRINTSCREEN" or "PRTSC" => "PrintScreen",
            _ => null,
        };
    }
}
