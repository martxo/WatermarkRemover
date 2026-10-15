using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public class FeatureContextTests
{
    [Fact]
    public void Capture_snapshots_settings_at_capture_time()
    {
        var settings = new AppSettings { OutputSuffix = "_before" };

        var context = FeatureContext.Capture(settings);
        settings.OutputSuffix = "_after";

        Assert.Equal("_before", context.Settings.OutputSuffix);
    }

    [Fact]
    public void Capture_deep_copies_the_suffix_list()
    {
        var settings = new AppSettings();

        var context = FeatureContext.Capture(settings);
        settings.QuickSuffixes.Add("发票");

        Assert.DoesNotContain("发票", context.Settings.QuickSuffixes);
    }

    [Fact]
    public void Context_settings_are_an_independent_instance()
    {
        var settings = new AppSettings();
        var context = FeatureContext.Capture(settings);

        Assert.NotSame(settings, context.Settings);
    }
}
