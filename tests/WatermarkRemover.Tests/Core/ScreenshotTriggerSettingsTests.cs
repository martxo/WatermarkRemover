using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public class ScreenshotTriggerSettingsTests
{
    private const double WorkWidth = 1000;
    private const double WorkHeight = 800;

    [Fact]
    public void Default_keeps_only_the_top_edge_enabled_and_centred()
    {
        var trigger = new ScreenshotTriggerSettings();

        Assert.True(trigger.Top.Enabled);
        Assert.False(trigger.Bottom.Enabled);
        Assert.False(trigger.Left.Enabled);
        Assert.False(trigger.Right.Enabled);

        // 默认与旧版「顶部 32px 热区」同厚度，长度/位置可配。
        Assert.Equal(ScreenshotTriggerSettings.DefaultThickness, trigger.Top.Thickness);
        Assert.Equal(ScreenshotTriggerSettings.DefaultLength, trigger.Top.Length);
        Assert.Equal(0.5, trigger.Top.Position);
    }

    [Fact]
    public void Clone_copies_every_edge_so_the_working_copy_is_isolated()
    {
        var trigger = new ScreenshotTriggerSettings();
        trigger.Left.Enabled = true;
        trigger.Left.Thickness = 48;

        ScreenshotTriggerSettings copy = trigger.Clone();
        copy.Left.Thickness = 96;
        copy.Top.Position = 0.1;

        Assert.Equal(48, trigger.Left.Thickness);
        Assert.Equal(0.5, trigger.Top.Position);
        Assert.True(copy.Left.Enabled);
    }

    [Fact]
    public void Zone_rect_sits_centred_on_the_top_edge()
    {
        var trigger = new ScreenshotTriggerSettings();
        trigger.Clamp(WorkWidth, WorkHeight);

        ScreenRect? rect = trigger.ZoneRect(ScreenEdge.Top, WorkWidth, WorkHeight);

        Assert.NotNull(rect);
        // 中心 = 0.5 × 1000 = 500；560 长 → 起点 220；厚度 32 伸入屏幕。
        Assert.Equal(220, rect!.Value.X, 3);
        Assert.Equal(0, rect.Value.Y, 3);
        Assert.Equal(560, rect.Value.Width, 3);
        Assert.Equal(32, rect.Value.Height, 3);
    }

    [Fact]
    public void Zone_rects_for_the_other_three_edges_anchor_to_their_own_edge()
    {
        var trigger = new ScreenshotTriggerSettings
        {
            Top = new TriggerZoneEdge { Enabled = false },
            Bottom = new TriggerZoneEdge { Enabled = true, Thickness = 40, Length = 300, Position = 0.25 },
            Left = new TriggerZoneEdge { Enabled = true, Thickness = 20, Length = 400, Position = 0.5 },
            Right = new TriggerZoneEdge { Enabled = true, Thickness = 20, Length = 400, Position = 0.75 },
        };
        trigger.Clamp(WorkWidth, WorkHeight);

        ScreenRect? bottom = trigger.ZoneRect(ScreenEdge.Bottom, WorkWidth, WorkHeight);
        ScreenRect? left = trigger.ZoneRect(ScreenEdge.Left, WorkWidth, WorkHeight);
        ScreenRect? right = trigger.ZoneRect(ScreenEdge.Right, WorkWidth, WorkHeight);

        Assert.Equal(0.25 * WorkWidth - 150, bottom!.Value.X, 3);
        Assert.Equal(WorkHeight - 40, bottom.Value.Y, 3);

        Assert.Equal(0, left!.Value.X, 3);
        Assert.Equal(0.5 * WorkHeight - 200, left.Value.Y, 3);

        Assert.Equal(WorkWidth - 20, right!.Value.X, 3);
        Assert.Equal(0.75 * WorkHeight - 200, right.Value.Y, 3);
    }

    [Fact]
    public void Hit_test_finds_the_top_zone_and_misses_below_it()
    {
        var trigger = new ScreenshotTriggerSettings();
        trigger.Clamp(WorkWidth, WorkHeight);

        Assert.Equal(ScreenEdge.Top, trigger.HitTest(300, 10, 0, 0, WorkWidth, WorkHeight));
        Assert.Null(trigger.HitTest(300, 40, 0, 0, WorkWidth, WorkHeight));
        // 热区右边界之外（220 + 560 = 780）。
        Assert.Null(trigger.HitTest(900, 10, 0, 0, WorkWidth, WorkHeight));
    }

    [Fact]
    public void A_disabled_edge_never_hits()
    {
        var trigger = new ScreenshotTriggerSettings { Top = new TriggerZoneEdge { Enabled = false } };
        trigger.Clamp(WorkWidth, WorkHeight);

        Assert.Null(trigger.ZoneRect(ScreenEdge.Top, WorkWidth, WorkHeight));
        Assert.Null(trigger.HitTest(300, 10, 0, 0, WorkWidth, WorkHeight));
    }

    [Fact]
    public void Clamp_pulls_out_of_range_values_back_onto_the_screen()
    {
        var trigger = new ScreenshotTriggerSettings
        {
            Top = new TriggerZoneEdge { Enabled = true, Thickness = 9999, Length = 5000, Position = 0.9 },
        };

        trigger.Clamp(WorkWidth, WorkHeight);

        Assert.Equal(ScreenshotTriggerSettings.MaxThickness, trigger.Top.Thickness);
        Assert.Equal((int)WorkWidth, trigger.Top.Length);
        // 长度占满整条边 → 位置归中。
        Assert.Equal(0.5, trigger.Top.Position);
    }

    [Fact]
    public void Position_clamps_so_the_zone_cannot_slip_off_the_edge()
    {
        var trigger = new ScreenshotTriggerSettings
        {
            Top = new TriggerZoneEdge { Enabled = true, Length = 400, Position = 0 },
        };

        trigger.Clamp(WorkWidth, WorkHeight);

        // 半长 200 → 最小位置 0.2：热区左沿正好贴屏幕左边。
        Assert.Equal(0.2, trigger.Top.Position, 6);
        ScreenRect? rect = trigger.ZoneRect(ScreenEdge.Top, WorkWidth, WorkHeight);
        Assert.Equal(0, rect!.Value.X, 3);
        Assert.Equal(ScreenEdge.Top, trigger.HitTest(5, 10, 0, 0, WorkWidth, WorkHeight));
        Assert.Null(trigger.HitTest(450, 10, 0, 0, WorkWidth, WorkHeight));
    }

    [Fact]
    public void Hit_test_ignores_a_cursor_outside_the_work_area()
    {
        var trigger = new ScreenshotTriggerSettings();
        trigger.Clamp(WorkWidth, WorkHeight);

        Assert.Null(trigger.HitTest(-5, 10, 0, 0, WorkWidth, WorkHeight));
        Assert.Null(trigger.HitTest(WorkWidth, 10, 0, 0, WorkWidth, WorkHeight));
        Assert.Null(trigger.HitTest(300, WorkHeight, 0, 0, WorkWidth, WorkHeight));
    }

    [Fact]
    public void With_every_edge_enabled_the_first_match_in_order_wins()
    {
        // 四边均占满整条边，角落热区两两重叠：按上→下→左→右取第一个命中，行为确定。
        var trigger = new ScreenshotTriggerSettings
        {
            Top = new TriggerZoneEdge { Enabled = true, Length = (int)WorkWidth },
            Bottom = new TriggerZoneEdge { Enabled = true, Length = (int)WorkWidth },
            Left = new TriggerZoneEdge { Enabled = true, Length = (int)WorkHeight },
            Right = new TriggerZoneEdge { Enabled = true, Length = (int)WorkHeight },
        };
        trigger.Clamp(WorkWidth, WorkHeight);
        Assert.Equal(ScreenEdge.Top, trigger.HitTest(5, 5, 0, 0, WorkWidth, WorkHeight));
        Assert.Equal(ScreenEdge.Bottom, trigger.HitTest(5, WorkHeight - 5, 0, 0, WorkWidth, WorkHeight));
        Assert.Equal(ScreenEdge.Left, trigger.HitTest(5, WorkHeight / 2, 0, 0, WorkWidth, WorkHeight));
        Assert.Equal(ScreenEdge.Right, trigger.HitTest(WorkWidth - 5, WorkHeight / 2, 0, 0, WorkWidth, WorkHeight));
    }

    [Fact]
    public void A_smaller_work_area_reclamps_the_zones()
    {
        var trigger = new ScreenshotTriggerSettings();
        trigger.Left.Enabled = true;
        trigger.Left.Length = 560;

        trigger.Clamp(400, 300);

        // 左边的「沿边」是高度：560 被收敛到 300（工作区高度），占满整条左边。
        Assert.Equal(300, trigger.Left.Length);
        Assert.Equal(0.5, trigger.Left.Position);
        ScreenRect? rect = trigger.ZoneRect(ScreenEdge.Left, 400, 300);
        Assert.Equal(0, rect!.Value.X, 3);
        Assert.Equal(0, rect.Value.Y, 3);
        Assert.Equal(300, rect.Value.Height, 3);
    }

    [Fact]
    public void AppSettings_persist_the_trigger_zones_across_a_round_trip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wmr-trigger-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings();
            settings.ScreenshotTrigger.Bottom.Enabled = true;
            settings.ScreenshotTrigger.Bottom.Thickness = 48;
            settings.ScreenshotTrigger.Bottom.Length = 720;
            settings.ScreenshotTrigger.Bottom.Position = 0.3;

            Assert.True(SettingsService.Save(settings, path));

            AppSettings loaded = SettingsService.Load(path);
            TriggerZoneEdge bottom = loaded.ScreenshotTrigger.Bottom;

            Assert.True(bottom.Enabled);
            Assert.Equal(48, bottom.Thickness);
            Assert.Equal(720, bottom.Length);
            Assert.Equal(0.3, bottom.Position, 6);
            // 未改的边保持默认。
            Assert.True(loaded.ScreenshotTrigger.Top.Enabled);
            Assert.False(loaded.ScreenshotTrigger.Left.Enabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Clone_of_settings_isolates_the_trigger_zones()
    {
        var settings = new AppSettings();
        settings.ScreenshotTrigger.Right.Enabled = true;

        AppSettings copy = settings.Clone();
        copy.ScreenshotTrigger.Right.Enabled = false;
        copy.ScreenshotTrigger.Right.Length = 120;

        Assert.True(settings.ScreenshotTrigger.Right.Enabled);
        Assert.Equal(ScreenshotTriggerSettings.DefaultLength, settings.ScreenshotTrigger.Right.Length);
    }
}
