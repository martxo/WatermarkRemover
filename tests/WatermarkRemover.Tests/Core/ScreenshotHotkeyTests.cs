using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// The shortcut text is the settings contract: parsing, canonical formatting and the fallback
/// used by the registrar must all agree, or a saved gesture silently stops working.
/// </summary>
public sealed class ScreenshotHotkeyTests
{
    [Fact]
    public void The_default_gesture_is_ctrl_alt_a()
    {
        Assert.Equal("Ctrl+Alt+A", ScreenshotHotkey.Default.ToString());
        Assert.True(ScreenshotHotkey.TryParse("Ctrl+Alt+A", out ScreenshotHotkey parsed));
        Assert.Equal(ScreenshotHotkey.Default, parsed);
    }

    [Theory]
    [InlineData("ctrl+alt+a")]
    [InlineData("CONTROL+ALT+A")]
    [InlineData("Ctrl + Alt + A")]
    public void Parsing_is_case_and_space_tolerant(string text)
    {
        Assert.True(ScreenshotHotkey.TryParse(text, out ScreenshotHotkey hotkey));
        Assert.Equal("Ctrl+Alt+A", hotkey.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Shift+F5", "Ctrl+Shift+F5")]
    [InlineData("Win+Alt+PrintScreen", "Alt+Win+PrintScreen")]
    public void Function_and_print_keys_are_supported(string text, string canonical)
    {
        Assert.True(ScreenshotHotkey.TryParse(text, out ScreenshotHotkey hotkey));
        Assert.Equal(canonical, hotkey.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Mouse1")]
    [InlineData("Ctrl+F25")]
    public void Malformed_gestures_are_rejected(string text)
    {
        Assert.False(ScreenshotHotkey.TryParse(text, out _));
    }

    [Fact]
    public void A_malformed_saved_value_falls_back_to_the_default()
    {
        Assert.Equal(ScreenshotHotkey.Default, ScreenshotHotkey.ParseOrDefault("Ctrl+"));
        Assert.Equal(ScreenshotHotkey.Default, ScreenshotHotkey.ParseOrDefault(null));
    }
}
