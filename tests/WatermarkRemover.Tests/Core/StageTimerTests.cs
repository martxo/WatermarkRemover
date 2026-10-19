using System.Diagnostics;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.Tests.Core;

public class StageTimerTests
{
    [Fact]
    public void StageEmitsScopeStageMsAndCumulativeWithInjectedClock()
    {
        var lines = new List<string>();
        // One second of stopwatch ticks per read (construction, then each Stage).
        long tick = 0;
        var clock = new Func<long>(() =>
        {
            long current = tick;
            tick += Stopwatch.Frequency;
            return current;
        });
        var timer = new StageTimer((message, _) => lines.Add(message), "文件.pdf", clock);

        timer.Stage("渲染页面", "2 页 200dpi");
        timer.Stage("第1页识别", "31 行");

        Assert.Equal(2, lines.Count);
        Assert.Equal("环节耗时[文件.pdf]：渲染页面 1000 ms（累计 1000 ms，2 页 200dpi）", lines[0]);
        Assert.Equal("环节耗时[文件.pdf]：第1页识别 1000 ms（累计 2000 ms，31 行）", lines[1]);
    }

    [Fact]
    public void StageOmitsDetailWhenAbsentAndEmitKeepsScopePrefix()
    {
        var lines = new List<string>();
        var timer = new StageTimer((message, _) => lines.Add(message), "批次", () => 0);

        timer.Stage("写出文本");
        timer.Emit("总计 0 ms（1 页）");

        Assert.Equal("环节耗时[批次]：写出文本 0 ms（累计 0 ms）", lines[0]);
        Assert.Equal("环节耗时[批次]：总计 0 ms（1 页）", lines[1]);
    }

    [Fact]
    public void NullTraceDisablesTimerAndNeverReadsTheClock()
    {
        int clockReads = 0;
        var timer = new StageTimer(null, "范围", () =>
        {
            clockReads++;
            return 0;
        });

        Assert.False(timer.Enabled);
        timer.Stage("渲染页面", "详情");
        timer.Emit("总计 0 ms");

        // Only the constructor reads the clock — a disabled timer costs nothing per stage.
        Assert.Equal(1, clockReads);
        Assert.Equal(0, timer.ElapsedMs);
    }

    [Fact]
    public void BlankScopeFallsBackToASanePrefix()
    {
        var lines = new List<string>();
        var timer = new StageTimer((message, _) => lines.Add(message), "  ", () => 0);

        timer.Emit("完成");

        Assert.Equal("环节耗时[执行]：完成", lines[0]);
    }
}
