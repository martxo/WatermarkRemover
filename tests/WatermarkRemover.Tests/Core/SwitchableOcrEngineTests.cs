using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.Tests.Core;

public class SwitchableOcrEngineTests
{
    private sealed class NamedEngine(string name) : IOcrEngine
    {
        public string DisplayName => name;

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { name });
    }

    [Fact]
    public async Task ForwardsCallsToCurrentEngineAndSwitchTakesEffect()
    {
        var proxy = new SwitchableOcrEngine(new NamedEngine("first"));
        Assert.Equal("first", proxy.DisplayName);
        Assert.Equal("first", (await proxy.RecognizeAsync(Array.Empty<byte>()))[0]);

        proxy.SwitchTo(new NamedEngine("second"));

        Assert.Equal("second", proxy.DisplayName);
        Assert.Equal("second", (await proxy.RecognizeAsync(Array.Empty<byte>()))[0]);
        Assert.Equal("second", proxy.Current.DisplayName);
    }

    [Fact]
    public void RejectsNullInitialAndSwitchTargets()
    {
        Assert.Throws<ArgumentNullException>(() => new SwitchableOcrEngine(null!));

        var proxy = new SwitchableOcrEngine(new NamedEngine("first"));
        Assert.Throws<ArgumentNullException>(() => proxy.SwitchTo(null!));
    }
}
