using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// The shortcut text is the settings contract: parsing, canonical formatting and the fallback
/// used by the registrar must all agree, or a saved gesture silently stops working.
/// </summary>
public sealed class ScreenshotHotkeyTests
{
    [Fact]
    public void The_default_gesture_is_the_single_key_f1()
    {
        // 1.16.81 起新安装默认 F1（单键）：RegisterHotKey 接受无修饰键（本机实测），
        // 代价是抢占 F1，由一次性提示与设置页文案说明。
        Assert.Equal("F1", ScreenshotHotkey.Default.ToString());
        Assert.False(ScreenshotHotkey.Default.HasModifier);
        Assert.True(ScreenshotHotkey.TryParse("F1", out ScreenshotHotkey parsed));
        Assert.Equal(ScreenshotHotkey.Default, parsed);
    }

    [Fact]
    public void The_shelf_shortcut_is_a_fixed_ctrl_alt_t()
    {
        Assert.Equal("Ctrl+Alt+T", ScreenshotHotkey.ShelfDefault.ToString());
        Assert.True(ScreenshotHotkey.TryParse("Ctrl+Alt+T", out ScreenshotHotkey parsed));
        Assert.Equal(ScreenshotHotkey.ShelfDefault, parsed);
    }

    [Fact]
    public void The_full_screen_shortcut_is_ctrl_alt_f_and_distinct_from_the_other_two()
    {
        Assert.Equal("Ctrl+Alt+F", ScreenshotHotkey.FullScreenDefault.ToString());
        Assert.NotEqual(ScreenshotHotkey.Default, ScreenshotHotkey.FullScreenDefault);
        Assert.NotEqual(ScreenshotHotkey.ShelfDefault, ScreenshotHotkey.FullScreenDefault);
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
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Mouse1")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    public void Malformed_gestures_are_rejected(string text)
    {
        Assert.False(ScreenshotHotkey.TryParse(text, out _));
    }

    // --- 单键热键 -----------------------------------------------------------------
    // Win32 RegisterHotKey 接受 fsModifiers=0（本机实测 F1/A/Space/PrintScreen 全部注册成功，
    // 撞键返回 1409），所以解析层不再强制修饰键；抢占按键的风险由设置页一次性提示兜住。

    [Theory]
    [InlineData("F1", "F1")]
    [InlineData("f2", "F2")]
    [InlineData("F12", "F12")]
    [InlineData("A", "A")]
    [InlineData("q", "Q")]
    [InlineData("7", "7")]
    [InlineData("PrintScreen", "PrintScreen")]
    [InlineData("PrtSc", "PrintScreen")]
    public void A_bare_key_parses_without_modifiers(string text, string canonical)
    {
        Assert.True(ScreenshotHotkey.TryParse(text, out ScreenshotHotkey hotkey));
        Assert.False(hotkey.HasModifier, $"{text} 应当没有修饰键");
        Assert.Equal(canonical, hotkey.ToString());
        Assert.Equal(canonical, hotkey.Key);
    }

    [Fact]
    public void A_bare_key_round_trips_through_its_text_form()
    {
        Assert.True(ScreenshotHotkey.TryParse("F1", out ScreenshotHotkey hotkey));
        Assert.Equal(hotkey, ScreenshotHotkey.ParseOrDefault(hotkey.ToString()));
    }

    [Fact]
    public void ParseOrDefault_keeps_a_saved_bare_key_instead_of_replacing_it()
    {
        Assert.False(ScreenshotHotkey.ParseOrDefault("F1").HasModifier);
        Assert.Equal("F1", ScreenshotHotkey.ParseOrDefault("F1").ToString());
    }

    [Fact]
    public void A_malformed_saved_value_falls_back_to_the_default()
    {
        Assert.Equal(ScreenshotHotkey.Default, ScreenshotHotkey.ParseOrDefault("Ctrl+"));
        Assert.Equal(ScreenshotHotkey.Default, ScreenshotHotkey.ParseOrDefault(null));
    }
}
