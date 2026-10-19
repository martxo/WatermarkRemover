using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Tests.Core;

public class LayoutStageChainTests
{
    private sealed class FakeStage(string name, bool passthrough = false) : ILayoutStage
    {
        public string Name => name;

        public bool IsPassthrough => passthrough;

        public Task<IReadOnlyList<LayoutRegion>> DetectRegionsAsync(byte[] imageBytes, IRegionProbe probe) =>
            Task.FromResult<IReadOnlyList<LayoutRegion>>(
                new[] { new LayoutRegion(new RectD(0, 0, 1, 1), "page") });
    }

    private sealed class FakeFactory(string name, bool available, bool throws = false) : ILayoutStageFactory
    {
        public string Name => name;

        public bool TryCreate(out ILayoutStage? stage, out string reason)
        {
            if (throws) throw new InvalidOperationException($"{name} crashed");
            stage = available ? new FakeStage(name) : null;
            reason = available ? string.Empty : $"{name} missing";
            return available;
        }
    }

    [Fact]
    public void PicksFirstAvailableBlockAndRecordsSkippedLinks()
    {
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new FakeFactory("first", available: false),
            new FakeFactory("second", available: true),
            new FakeFactory("third", available: true),
        });

        Assert.Equal("second", chain.Stage.Name);
        Assert.True(chain.HasStage);
        // Probing stops at the first available block; the third factory is never probed.
        Assert.Equal(new[] { "first", "second" }, chain.Links.Select(l => l.Name));
    }

    [Fact]
    public void FallsBackToFullImagePassthroughWhenNothingUsable()
    {
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new FakeFactory("first", available: false),
            new FakeFactory("second", available: false),
        });

        // No unavailable stub — the always-usable full-image block takes over.
        Assert.IsType<FullImageLayoutStage>(chain.Stage);
        Assert.True(chain.Stage.IsPassthrough);
        Assert.False(chain.HasStage);
        Assert.Contains("second：不可用（second missing）", chain.Describe());
    }

    [Fact]
    public void EmptyRegistrationStillYieldsPassthrough()
    {
        var chain = new LayoutStageChain(Array.Empty<ILayoutStageFactory>());

        Assert.IsType<FullImageLayoutStage>(chain.Stage);
        Assert.True(chain.Stage.IsPassthrough);
    }

    [Fact]
    public void ThrowingFactoryIsRecordedAndChainKeepsDegrading()
    {
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new FakeFactory("broken", available: true, throws: true),
            new FakeFactory("healthy", available: true),
        });

        Assert.Equal("healthy", chain.Stage.Name);
        Assert.Contains("broken crashed", chain.Links[0].Reason);
    }

    [Fact]
    public void SelectReturnsPinnedBlockAndProbesRemainingFactories()
    {
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new FakeFactory("first", available: true),
            new FakeFactory("second", available: true),
        });

        // Startup still stops at the first usable block — the extra probe only happens on demand.
        Assert.Equal("first", chain.Stage.Name);
        Assert.Equal(new[] { "first" }, chain.Links.Select(l => l.Name));

        Assert.Equal("second", chain.Select("second").Name);
        Assert.Equal(new[] { "first", "second" }, chain.Links.Select(l => l.Name));
    }

    [Fact]
    public void SelectFallsBackToAutomaticChainWhenPreferenceIsUnusable()
    {
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new FakeFactory("first", available: true),
            new FakeFactory("second", available: false),
        });

        // Unavailable, unknown, empty and null preferences all degrade to the priority head.
        Assert.Equal("first", chain.Select("second").Name);
        Assert.Equal("first", chain.Select("removed-block").Name);
        Assert.Equal("first", chain.Select(string.Empty).Name);
        Assert.Equal("first", chain.Select(null).Name);
    }

    [Fact]
    public void EnsureAllProbedProbesEveryFactoryExactlyOnce()
    {
        int probes = 0;
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new DelegateLayoutStageFactory("usable", () =>
            {
                probes++;
                return (true, new FakeStage("usable"), string.Empty);
            }),
            new DelegateLayoutStageFactory("missing", () =>
            {
                probes++;
                return (false, null, "model file absent");
            }),
        });

        Assert.Equal(1, probes); // constructor stopped at the first usable block

        chain.EnsureAllProbed();
        chain.EnsureAllProbed(); // idempotent — never re-probes a factory

        Assert.Equal(2, probes);
        Assert.Equal(new[] { "usable", "missing" }, chain.Links.Select(l => l.Name));
        Assert.Equal("model file absent", chain.Links[1].Reason);
    }

    [Fact]
    public void DescribeListsEveryProbedLink()
    {
        var chain = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new FakeFactory("first", available: false),
            new FakeFactory("second", available: true),
        });

        string description = chain.Describe();
        Assert.Contains("first：不可用（first missing）", description);
        Assert.Contains("second：可用", description);
    }
}
