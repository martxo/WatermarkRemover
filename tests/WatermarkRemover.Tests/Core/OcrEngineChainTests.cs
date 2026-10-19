using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.Tests.Core;

public class OcrEngineChainTests
{
    private sealed class FakeEngine(string name) : IOcrEngine
    {
        public string DisplayName => name;

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { name });
    }

    private sealed class FakeFactory(string name, bool available, bool throws = false) : IOcrEngineFactory
    {
        public string Name => name;

        public bool TryCreate(out IOcrEngine? engine, out string reason)
        {
            if (throws) throw new InvalidOperationException($"{name} crashed");
            engine = available ? new FakeEngine(name) : null;
            reason = available ? string.Empty : $"{name} missing";
            return available;
        }
    }

    [Fact]
    public void PicksFirstAvailableEngineAndRecordsSkippedLinks()
    {
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new FakeFactory("first", available: false),
            new FakeFactory("second", available: true),
            new FakeFactory("third", available: true),
        });

        Assert.Equal("second", chain.Engine.DisplayName);
        Assert.True(chain.HasEngine);
        // Probing stops at the first available engine; the third is never probed.
        Assert.Equal(new[] { "first", "second" }, chain.Links.Select(l => l.Name));
    }

    [Fact]
    public async Task FallbackStubCarriesAllFailureReasonsWhenNothingUsable()
    {
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new FakeFactory("first", available: false),
            new FakeFactory("second", available: false),
        });

        Assert.False(chain.HasEngine);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.Engine.RecognizeAsync(Array.Empty<byte>()));
        Assert.Contains("first missing", ex.Message);
        Assert.Contains("second missing", ex.Message);
    }

    [Fact]
    public void ThrowingEngineIsRecordedAndChainKeepsDegrading()
    {
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new FakeFactory("broken", available: true, throws: true),
            new FakeFactory("healthy", available: true),
        });

        Assert.Equal("healthy", chain.Engine.DisplayName);
        Assert.Contains("broken crashed", chain.Links[0].Reason);
    }

    [Fact]
    public void DescribeListsEveryProbedLink()
    {
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new FakeFactory("first", available: false),
            new FakeFactory("second", available: true),
        });

        string description = chain.Describe();
        Assert.Contains("first：不可用（first missing）", description);
        Assert.Contains("second：可用", description);
    }

    [Fact]
    public async Task EmptyChainYieldsStubMentioningNoRegistration()
    {
        var chain = new OcrEngineChain(Array.Empty<IOcrEngineFactory>());

        Assert.False(chain.HasEngine);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.Engine.RecognizeAsync(Array.Empty<byte>()));
        Assert.Contains("未注册任何 OCR 引擎", ex.Message);
    }

    [Fact]
    public void SelectReturnsPinnedEngineAndProbesRemainingFactories()
    {
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new FakeFactory("first", available: true),
            new FakeFactory("second", available: true),
        });

        // Startup still stops at the first usable engine — the extra probe only happens on demand.
        Assert.Equal("first", chain.Engine.DisplayName);
        Assert.Equal(new[] { "first" }, chain.Links.Select(l => l.Name));

        Assert.Equal("second", chain.Select("second").DisplayName);
        Assert.Equal(new[] { "first", "second" }, chain.Links.Select(l => l.Name));
    }

    [Fact]
    public void SelectFallsBackToAutomaticChainWhenPreferenceIsUnusable()
    {
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new FakeFactory("first", available: true),
            new FakeFactory("second", available: false),
        });

        // Unavailable, unknown, empty and null preferences all degrade to the priority head.
        Assert.Equal("first", chain.Select("second").DisplayName);
        Assert.Equal("first", chain.Select("removed-engine").DisplayName);
        Assert.Equal("first", chain.Select(string.Empty).DisplayName);
        Assert.Equal("first", chain.Select(null).DisplayName);
    }

    [Fact]
    public void EnsureAllProbedProbesEveryFactoryExactlyOnce()
    {
        int probes = 0;
        var chain = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new DelegateOcrEngineFactory("usable", () =>
            {
                probes++;
                return (true, new FakeEngine("usable"), string.Empty);
            }),
            new DelegateOcrEngineFactory("missing", () =>
            {
                probes++;
                return (false, null, "not on this machine");
            }),
        });

        Assert.Equal(1, probes); // constructor stopped at the first usable engine

        chain.EnsureAllProbed();
        chain.EnsureAllProbed(); // idempotent — never re-probes a factory

        Assert.Equal(2, probes);
        Assert.Equal(new[] { "usable", "missing" }, chain.Links.Select(l => l.Name));
        Assert.Equal("not on this machine", chain.Links[1].Reason);
    }
}
