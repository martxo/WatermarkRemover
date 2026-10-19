using System.Text;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Tests.Core.Recognition;

namespace WatermarkRemover.Tests.Core;

public class PipelineOcrEngineTests
{
    private sealed class FakeStage(
        string name,
        IReadOnlyList<LayoutRegion>? regions = null,
        bool passthrough = false,
        bool throws = false) : ILayoutStage
    {
        public string Name => name;

        public bool IsPassthrough => passthrough;

        public Task<IReadOnlyList<LayoutRegion>> DetectRegionsAsync(byte[] imageBytes, IRegionProbe probe)
        {
            if (throws) throw new InvalidOperationException("layout model crashed");
            return Task.FromResult(regions ?? new LayoutRegion[] { new(new RectD(0, 0, 1, 1), "page") });
        }
    }

    private sealed class FakeEngine : IOcrEngine
    {
        public Func<byte[], IReadOnlyList<string>> Handler { get; init; } = _ => new[] { "full" };

        public string DisplayName { get; init; } = "假引擎";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult(Handler(imageBytes));
    }

    /// <summary>An engine that also reports line bounds — what the anchor blocks need from a probe.</summary>
    private sealed class BoundedEngine : IBoundedOcrEngine
    {
        /// <summary>What the anchor probe returns; bounds let template blocks cut bands.</summary>
        public IReadOnlyList<RecognizedLine> ProbeLines { get; init; } =
            new[] { new RecognizedLine("名称", new RectD(0.1, 0.2, 0.3, 0.05)) };

        /// <summary>What the ordinary recognition path returns — per-region crops ride through it.</summary>
        public Func<byte[], IReadOnlyList<string>> Handler { get; init; } = _ => new[] { "bounded" };

        public string DisplayName => "有坐标引擎";

        public int RecognizeCalls { get; private set; }

        public int BoundsCalls { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            RecognizeCalls++;
            return Task.FromResult(Handler(imageBytes));
        }

        public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
        {
            BoundsCalls++;
            return Task.FromResult(ProbeLines);
        }
    }

    private static string Format(RectD r) =>
        FormattableString.Invariant($"{r.X:0.######},{r.Y:0.######},{r.Width:0.######},{r.Height:0.######}");

    [Fact]
    public async Task PassthroughBlockIsByteIdenticalAndNeverCrops()
    {
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            new FakeStage("整图直通", passthrough: true),
            new FakeEngine(),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
        Assert.Equal(0, crops); // passthrough never touches the codec
        Assert.Equal("假引擎", pipeline.DisplayName); // composition collapses to the engine
    }

    [Fact]
    public async Task CutsRegionsInReadingOrderWithPaddingAndComposesDisplayName()
    {
        LayoutRegion bottom = new(new RectD(0.1, 0.6, 0.4, 0.2), "text");
        LayoutRegion top = new(new RectD(0.2, 0.1, 0.3, 0.2), "text");
        var stage = new FakeStage("版面块", new[] { bottom, top }); // handed over bottom-first

        var inner = new FakeEngine
        {
            Handler = bytes => new[] { Encoding.UTF8.GetString(bytes) },
        };
        byte[] Crop(byte[] image, RectD rect) => Encoding.UTF8.GetBytes(Format(rect));

        var pipeline = new PipelineOcrEngine(stage, inner, Crop);
        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(2, lines.Count);
        // Top row first (reading order), each crop grown by 2% per side.
        Assert.Equal("0.194,0.096,0.312,0.208", lines[0]);
        Assert.Equal("0.092,0.596,0.416,0.208", lines[1]);
        Assert.Equal("版面块 + 假引擎", pipeline.DisplayName);
    }

    [Fact]
    public async Task SingleFullImageRegionShortCircuitsToDirectRecognition()
    {
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { new LayoutRegion(new RectD(0, 0, 1, 1), "page") }),
            new FakeEngine(),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
        Assert.Equal(0, crops);
    }

    [Fact]
    public async Task SinglePartialRegionIsStillCropped()
    {
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { new LayoutRegion(new RectD(0.1, 0.1, 0.5, 0.5), "text") }),
            new FakeEngine { Handler = _ => new[] { "partial" } },
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "partial" }, lines);
        Assert.Equal(1, crops);
    }

    [Fact]
    public async Task StageFailureFallsBackToFullImageRecognition()
    {
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", throws: true),
            new FakeEngine(),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
        Assert.Equal(0, crops);
    }

    [Fact]
    public async Task PageCoveringRegionIsDroppedWhenFinerRegionsExist()
    {
        // PP-DocLayout emits a page-level "image"/"table" box alongside real regions; keeping it
        // would re-OCR the whole page and duplicate every smaller crop's lines.
        LayoutRegion pageLevel = new(new RectD(0.05, 0.0, 0.88, 1.0), "image");
        LayoutRegion small = new(new RectD(0.2, 0.1, 0.3, 0.2), "text");
        var stage = new FakeStage("版面块", new[] { pageLevel, small });

        var crops = new List<string>();
        var inner = new FakeEngine { Handler = bytes => new[] { Encoding.UTF8.GetString(bytes) } };
        var pipeline = new PipelineOcrEngine(stage, inner, (_, rect) =>
        {
            crops.Add(Format(rect));
            return Array.Empty<byte>();
        });

        await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Single(crops); // only the fine region was cropped
        Assert.Equal("0.194,0.096,0.312,0.208", crops[0]);
    }

    [Fact]
    public async Task OverlappingRegionsEmitEachLineOnceButKeepIntraRegionDuplicates()
    {
        // Real detectors hand out overlapping boxes, so the same row gets cropped twice — the
        // pipeline must not echo it. A line repeating inside ONE crop is real text though.
        LayoutRegion a = new(new RectD(0.1, 0.1, 0.3, 0.2), "text");
        LayoutRegion b = new(new RectD(0.12, 0.12, 0.3, 0.2), "text");
        var stage = new FakeStage("版面块", new[] { a, b });

        int call = 0;
        var inner = new FakeEngine
        {
            // Region 1 repeats a line internally; region 2 (the overlapping crop) sees it again.
            Handler = _ => ++call == 1 ? new[] { "名称", "名称" } : new[] { "名称", "金额" },
        };
        var pipeline = new PipelineOcrEngine(stage, inner, (_, _) => Array.Empty<byte>());

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "名称", "名称", "金额" }, lines);
    }

    [Fact]
    public async Task ProbeCarriesBoundsWhenEngineSupportsPositioning()
    {
        var pipeline = new PipelineOcrEngine(new FakeStage("版面块"), new BoundedEngine());
        var probe = (IRegionProbe)pipeline;

        IReadOnlyList<RecognizedLine> lines = await probe.RecognizeAsync(new byte[] { 1 });

        Assert.Equal("名称", lines[0].Text);
        Assert.NotNull(lines[0].Bounds);
        Assert.Equal(0.1, lines[0].Bounds!.Value.X, 6);
    }

    [Fact]
    public async Task ProbeDegradesToTextOnlyWhenEngineHasNoPositioning()
    {
        var pipeline = new PipelineOcrEngine(new FakeStage("版面块"), new FakeEngine());
        var probe = (IRegionProbe)pipeline;

        IReadOnlyList<RecognizedLine> lines = await probe.RecognizeAsync(new byte[] { 1 });

        Assert.Equal("full", lines[0].Text);
        Assert.Null(lines[0].Bounds); // anchor blocks then find no anchors and fall back
    }

    // ---- 组合场景：积木（版面） × 引擎能力 × 裁剪器的每一种搭配 ----

    [Fact]
    public void RejectsNullStageAndEngine()
    {
        Assert.Throws<ArgumentNullException>(() => new PipelineOcrEngine(null!, new FakeEngine()));
        Assert.Throws<ArgumentNullException>(() => new PipelineOcrEngine(new FakeStage("x"), null!));
    }

    [Fact]
    public async Task RegionBlockWithoutCodecRecognisesDirectly()
    {
        // crop is optional in the ctor: without the App-side codec a region block must not fail,
        // it behaves exactly like the direct path.
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { new LayoutRegion(new RectD(0.1, 0.1, 0.4, 0.3), "text") }),
            new FakeEngine());

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
    }

    [Fact]
    public async Task EmptyRegionListFallsBackToDirectRecognition()
    {
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", Array.Empty<LayoutRegion>()),
            new FakeEngine(),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
        Assert.Equal(0, crops);
    }

    [Theory]
    [InlineData(0, 0, 0.1, 0.1, "0,0,0.102,0.102")] // 左上角：外扩被图像边缘夹住
    [InlineData(0.9, 0.9, 0.1, 0.1, "0.898,0.898,0.102,0.102")] // 右下角：外扩被图像边缘夹住
    public async Task CropExpansionClampsAtImageEdges(double x, double y, double w, double h, string expected)
    {
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { new LayoutRegion(new RectD(x, y, w, h), "text") }),
            new FakeEngine { Handler = bytes => new[] { Encoding.UTF8.GetString(bytes) } },
            (_, rect) => Encoding.UTF8.GetBytes(Format(rect)));

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(expected, Assert.Single(lines));
    }

    [Fact]
    public async Task CrossRegionDuplicateLinesAreComparedTrimmed()
    {
        LayoutRegion top = new(new RectD(0.1, 0.1, 0.3, 0.2), "text");
        LayoutRegion lower = new(new RectD(0.1, 0.4, 0.3, 0.2), "text");
        int call = 0;
        var inner = new FakeEngine
        {
            Handler = _ => ++call == 1 ? new[] { " 名称", "类型" } : new[] { "名称", "住所" },
        };
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { top, lower }), inner, (_, _) => Array.Empty<byte>());

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        // Region 2's "名称" differs only in surrounding whitespace → suppressed; region 1's
        // original spacing is preserved verbatim.
        Assert.Equal(new[] { " 名称", "类型", "住所" }, lines);
    }

    [Fact]
    public async Task PageCoveringFilterKeepsTallNarrowRegions()
    {
        // h ≥ 0.9 but w < 0.85 — a sidebar column, not a page-level box: must be kept.
        LayoutRegion sidebar = new(new RectD(0.1, 0.0, 0.3, 1.0), "text");
        LayoutRegion small = new(new RectD(0.5, 0.2, 0.3, 0.2), "text");
        var inner = new FakeEngine { Handler = bytes => new[] { Encoding.UTF8.GetString(bytes) } };
        var crops = new List<string>();
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { sidebar, small }),
            inner,
            (_, rect) =>
            {
                crops.Add(Format(rect));
                return Encoding.UTF8.GetBytes(Format(rect));
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(2, crops.Count);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task PageCoveringFilterKeepsRegionsWhenEveryCandidateCoversThePage()
    {
        // If the filter would empty the list it keeps everything — better two heavy crops than
        // silently returning nothing.
        LayoutRegion wide = new(new RectD(0, 0, 0.9, 1.0), "table");
        LayoutRegion tall = new(new RectD(0.05, 0, 0.9, 0.95), "image");
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { wide, tall }),
            new FakeEngine(),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(2, crops);
    }

    [Fact]
    public async Task RegionBlockReadsBoundedEngineThroughPlainPath()
    {
        // 搭配：区域积木 × 有坐标引擎 —— crops travel through RecognizeAsync (every engine has
        // it); the bounds capability is only asked for when a block actually needs the probe.
        var crops = new List<string>();
        var bounded = new BoundedEngine { Handler = bytes => new[] { Encoding.UTF8.GetString(bytes) } };
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[]
            {
                new LayoutRegion(new RectD(0.1, 0.1, 0.3, 0.2), "text"),
                new LayoutRegion(new RectD(0.1, 0.5, 0.3, 0.2), "text"),
            }),
            bounded,
            (_, rect) =>
            {
                crops.Add(Format(rect));
                return Encoding.UTF8.GetBytes(Format(rect));
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(2, bounded.RecognizeCalls);
        Assert.Equal(0, bounded.BoundsCalls);
        Assert.Equal(crops, lines);
    }

    [Fact]
    public async Task PassthroughBlockUsesBoundedEngineRecognizePath()
    {
        // 搭配：整图直通 × 有坐标引擎 —— no crop, plain recognition, and the probe (used by
        // anchor blocks) still carries bounds.
        int crops = 0;
        var bounded = new BoundedEngine { Handler = _ => new[] { "整页" } };
        var pipeline = new PipelineOcrEngine(
            new FakeStage("整图直通", passthrough: true),
            bounded,
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "整页" }, lines);
        Assert.Equal(1, bounded.RecognizeCalls);
        Assert.Equal(0, bounded.BoundsCalls);
        Assert.Equal(0, crops);
        Assert.Equal("有坐标引擎", pipeline.DisplayName); // passthrough collapses to the engine
    }

    [Fact]
    public async Task AnchorTemplateBlockCombinesWithBoundedEngineProbe()
    {
        // 搭配：营业执照模板定位 × 有坐标引擎 —— the full loop: one bounded probe pass locates
        // the label anchors, the template cuts one band per row, each band is then recognised.
        var bounded = new BoundedEngine
        {
            ProbeLines = new[]
            {
                new RecognizedLine("名称：张三", new RectD(0.1, 0.2, 0.5, 0.03)),
                new RecognizedLine("法定代表人：李四", new RectD(0.1, 0.4, 0.5, 0.03)),
            },
            Handler = bytes => new[] { Encoding.UTF8.GetString(bytes) },
        };
        var crops = new List<string>();
        var pipeline = new PipelineOcrEngine(
            LicenseTemplateStage.Instance,
            bounded,
            (_, rect) =>
            {
                crops.Add(Format(rect));
                return Encoding.UTF8.GetBytes(Format(rect));
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(1, bounded.BoundsCalls); // one full-image probe supplied every anchor
        Assert.Equal(2, crops.Count); // one crop per label band
        Assert.Equal("0.082,0.196,0.918,0.208", crops[0]); // band 1: row 1 top → row 2 top, +2% pad
        Assert.Equal("0.082,0.388,0.918,0.612", crops[1]); // band 2: row 2 top → page bottom
        Assert.Equal(crops, lines);
    }

    [Fact]
    public async Task AnchorTemplateBlockWithPlainEngineDegradesToFullImage()
    {
        // 搭配：营业执照模板定位 × 无坐标引擎 —— probe lines come without bounds, no anchors
        // can be placed, so the template falls back to one full-image recognition.
        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            LicenseTemplateStage.Instance,
            new FakeEngine(),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
        Assert.Equal(0, crops);
    }

    [Theory]
    [InlineData(null, null, "引擎甲", 0)] // 两环自动：直通头 × 引擎链头
    [InlineData(null, "引擎乙", "引擎乙", 0)] // 指定引擎 × 直通积木
    [InlineData("版面块", null, "版面块 + 引擎甲", 1)] // 指定积木 × 自动引擎
    [InlineData("版面块", "引擎乙", "版面块 + 引擎乙", 1)] // 双指定
    [InlineData("未知积木", "未知引擎", "引擎甲", 0)] // 双失效偏好全部回退到自动头
    public async Task EveryPreferenceComboComposesAWorkingPipeline(
        string? layout, string? engine, string expectedDisplayName, int expectedCrops)
    {
        // Mirrors AppOcrEngineChain.ApplyPipeline: chain.Select for both preferences, then wrap.
        var layouts = new LayoutStageChain(new ILayoutStageFactory[]
        {
            new DelegateLayoutStageFactory(
                "整图直通",
                () => (true, new FakeStage("整图直通", passthrough: true), string.Empty)),
            new DelegateLayoutStageFactory(
                "版面块",
                () => (true, new FakeStage("版面块",
                    new[] { new LayoutRegion(new RectD(0.1, 0.1, 0.4, 0.3), "text") }), string.Empty)),
        });
        var engines = new OcrEngineChain(new IOcrEngineFactory[]
        {
            new DelegateOcrEngineFactory(
                "引擎甲", () => (true, new FakeEngine { DisplayName = "引擎甲" }, string.Empty)),
            new DelegateOcrEngineFactory(
                "引擎乙", () => (true, new FakeEngine { DisplayName = "引擎乙" }, string.Empty)),
        });

        int crops = 0;
        var pipeline = new PipelineOcrEngine(
            layouts.Select(layout),
            engines.Select(engine),
            (_, _) =>
            {
                crops++;
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(expectedDisplayName, pipeline.DisplayName);
        Assert.Equal(expectedCrops, crops);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public async Task SwitchableProxyHandsOverBetweenPipelineCombos()
    {
        // Settings save switches the stable proxy's target: features keep their one reference
        // and every later call rides the newly composed combo.
        int cropsA = 0, cropsB = 0;
        var proxy = new SwitchableOcrEngine(new PipelineOcrEngine(
            new FakeStage("整图直通", passthrough: true),
            new FakeEngine { DisplayName = "引擎甲" },
            (_, _) =>
            {
                cropsA++;
                return Array.Empty<byte>();
            }));

        Assert.Equal("引擎甲", proxy.DisplayName);
        await proxy.RecognizeAsync(new byte[] { 1 });
        Assert.Equal(0, cropsA);

        proxy.SwitchTo(new PipelineOcrEngine(
            new FakeStage("版面块", new[] { new LayoutRegion(new RectD(0.1, 0.1, 0.4, 0.3), "text") }),
            new FakeEngine { DisplayName = "引擎乙" },
            (_, _) =>
            {
                cropsB++;
                return Array.Empty<byte>();
            }));

        IReadOnlyList<string> lines = await proxy.RecognizeAsync(new byte[] { 1 });

        Assert.Equal("版面块 + 引擎乙", proxy.DisplayName);
        Assert.Equal(1, cropsB);
        Assert.Equal(new[] { "full" }, lines);
    }

    [Fact]
    public async Task TraceRecordsOneLineForPassthroughRecognition()
    {
        var trace = new List<string>();
        var pipeline = new PipelineOcrEngine(
            new FakeStage("整图直通", passthrough: true),
            new FakeEngine(),
            trace: (message, _) => trace.Add(message));

        await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Single(trace, l => l.Contains("环节耗时[管线调用") && l.Contains("：整图直通识别") && l.Contains("1 行"));
        Assert.All(trace, l => Assert.Contains(" ms", l));
    }

    [Fact]
    public async Task TraceAggregatesAllRegionsIntoOneStageLine()
    {
        // The agreed granularity: detection, one aggregated recognition line (regions + dedup
        // drops) and a per-page total — never one line per crop.
        LayoutRegion first = new(new RectD(0.1, 0.1, 0.4, 0.3), "text");
        LayoutRegion second = new(new RectD(0.1, 0.5, 0.4, 0.3), "text");
        var trace = new List<string>();
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { first, second }),
            new FakeEngine { Handler = _ => new[] { "重复行" } },
            (_, _) => Array.Empty<byte>(),
            (message, _) => trace.Add(message));

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "重复行" }, lines); // second region's copy suppressed by dedup
        Assert.Contains(trace, l => l.Contains("：版面检测") && l.Contains("检出 2 区"));
        Assert.Single(trace, l => l.Contains("：区域识别") && l.Contains("2 区，去重丢弃 1 行"));
        Assert.Contains(trace, l => l.Contains("：本页合计"));
    }

    [Fact]
    public async Task TraceReportsLayoutCrashAndFallbackToFullImage()
    {
        var trace = new List<string>();
        var pipeline = new PipelineOcrEngine(
            new FakeStage("坏块", throws: true),
            new FakeEngine(),
            (_, _) => Array.Empty<byte>(),
            (message, _) => trace.Add(message));

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "full" }, lines);
        Assert.Contains(trace, l => l.Contains("版面检测失败，回退整图") && l.Contains("layout model crashed"));
        Assert.Contains(trace, l => l.Contains("无可切分区域，回退整图"));
        Assert.Contains(trace, l => l.Contains("：整图识别"));
    }

    // ---- 识别报告：scope 激活时，同一次识别带上坐标并把页面记入 RecognitionRunScope ----

    [Fact]
    public async Task ScopeActiveMapsRegionLocalBoundsBackToPageSpace()
    {
        using var scope = RecognitionRunScope.Begin("doc.pdf", "OCR 识别");
        var bounded = new BoundedEngine
        {
            ProbeLines = new[] { new RecognizedLine("字段", new RectD(0.1, 0.2, 0.5, 0.25)) },
        };
        string cropSeen = "";
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[] { new LayoutRegion(new RectD(0.1, 0.1, 0.3, 0.2), "text") }),
            bounded,
            (_, rect) =>
            {
                cropSeen = Format(rect);
                return Array.Empty<byte>();
            });

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(PngHeader.Build(800, 600));

        Assert.Equal(new[] { "字段" }, lines);
        Assert.Equal(1, bounded.BoundsCalls);
        Assert.Equal(0, bounded.RecognizeCalls); // scope + bounded engine = one bounds pass total
        Assert.Equal("0.094,0.096,0.312,0.208", cropSeen);

        RecordedPage page = Assert.Single(scope.Pages);
        Assert.Equal(800, page.WidthPx);
        Assert.Equal(600, page.HeightPx);
        Assert.Equal("横版", page.Orientation);
        Assert.Equal("版面块", page.LayoutStage);
        Assert.Equal("有坐标引擎", page.EngineName);
        Assert.Single(page.Regions);

        // Crop-local (0.1,0.2,0.5,0.25) mapped back through cropRect (0.094,0.096,0.312,0.208).
        RecognizedLine line = Assert.Single(page.Lines);
        Assert.Equal("字段", line.Text);
        RectD b = line.Bounds!.Value;
        Assert.Equal(0.1252, b.X, 6);
        Assert.Equal(0.1376, b.Y, 6);
        Assert.Equal(0.156, b.Width, 6);
        Assert.Equal(0.052, b.Height, 6);
    }

    [Fact]
    public async Task ScopeActivePassthroughRecordsPageWithBounds()
    {
        using var scope = RecognitionRunScope.Begin("doc.pdf", "OCR 识别");
        var bounded = new BoundedEngine();
        var pipeline = new PipelineOcrEngine(new FakeStage("整图直通", passthrough: true), bounded);

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(PngHeader.Build(600, 800));

        Assert.Equal(new[] { "名称" }, lines);
        Assert.Equal(1, bounded.BoundsCalls);
        Assert.Equal(0, bounded.RecognizeCalls);

        RecordedPage page = Assert.Single(scope.Pages);
        Assert.Equal(600, page.WidthPx);
        Assert.Equal(800, page.HeightPx);
        Assert.Equal("竖版", page.Orientation);
        Assert.Equal("整图直通", page.LayoutStage);
        Assert.Equal("有坐标引擎", page.EngineName);
        Assert.Empty(page.Regions); // direct pass: no split took place
        RecognizedLine line = Assert.Single(page.Lines);
        Assert.NotNull(line.Bounds); // bounds ride through untouched (no crop to map through)
        Assert.Equal(0.1, line.Bounds!.Value.X, 6);
        Assert.Equal(0.2, line.Bounds.Value.Y, 6);
    }

    [Fact]
    public async Task ScopeRecordsPageEvenWhenLayoutCrashes()
    {
        using var scope = RecognitionRunScope.Begin("doc.pdf", "OCR 识别");
        var engine = new FakeEngine { Handler = _ => new[] { "整页文字" } };
        var pipeline = new PipelineOcrEngine(
            new FakeStage("坏块", throws: true), engine, (_, _) => Array.Empty<byte>());

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        Assert.Equal(new[] { "整页文字" }, lines);
        RecordedPage page = Assert.Single(scope.Pages);
        Assert.Null(page.WidthPx);      // non-PNG input → unknown size, reported honestly
        Assert.Null(page.HeightPx);
        Assert.Null(page.Orientation);
        Assert.Empty(page.Regions);     // fallback reads the full image: record no split
        Assert.Equal("坏块", page.LayoutStage);
        Assert.Equal("假引擎", page.EngineName);
        RecognizedLine line = Assert.Single(page.Lines);
        Assert.Null(line.Bounds);       // plain path: no coordinates without a bounded engine
    }

    [Fact]
    public async Task ScopeWithUnboundedEngineKeepsDedupAndNullBounds()
    {
        using var scope = RecognitionRunScope.Begin("doc.pdf", "OCR 识别");
        int call = 0;
        var engine = new FakeEngine
        {
            Handler = _ => ++call == 1 ? new[] { "名称", "名称" } : new[] { "名称", "金额" },
        };
        var pipeline = new PipelineOcrEngine(
            new FakeStage("版面块", new[]
            {
                new LayoutRegion(new RectD(0.1, 0.1, 0.3, 0.2), "text"),
                new LayoutRegion(new RectD(0.12, 0.12, 0.3, 0.2), "text"),
            }),
            engine,
            (_, _) => Array.Empty<byte>());

        IReadOnlyList<string> lines = await pipeline.RecognizeAsync(new byte[] { 1 });

        // Recording must not perturb dedup: intra-region duplicates stay, cross-region copies drop.
        Assert.Equal(new[] { "名称", "名称", "金额" }, lines);
        Assert.Equal(2, call); // one plain recognition per region — scope did not add a pass

        RecordedPage page = Assert.Single(scope.Pages);
        Assert.Equal(2, page.Regions.Count);
        Assert.Equal(3, page.Lines.Count);
        Assert.All(page.Lines, l => Assert.Null(l.Bounds));
    }
}
