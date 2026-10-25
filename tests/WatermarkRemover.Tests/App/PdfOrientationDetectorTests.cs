using SkiaSharp;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Tests.App;

public class PdfOrientationDetectorTests
{
    /// <summary>
    /// The sideways-naming regression (v1.15.32, 2026-10-21): the watermark (imported PDF)
    /// naming chain rendered the first page and OCR'd it as-is — imported files never pass
    /// through the scan flow's auto-orientation, so a lying-down page read as garbage and
    /// naming silently kept the timestamp name. The chain now routes starved pages through an
    /// injected upright pass built on this detector. Source-level guard: the upright pass must
    /// sit AFTER the first recognition attempt (upright pages must never pay for it) and BEFORE
    /// the retry recognition, and the app must wire the detector into the watermark registration.
    /// </summary>
    [Fact]
    public void Watermark_naming_orientes_the_page_before_recognising_it()
    {
        static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(FindSolutionRoot(), Path.Combine(parts)));

        string feature = Read("src", "WatermarkRemover.Core", "Features", "WatermarkFeature.cs");
        Assert.Contains("Func<byte[], Task<byte[]>>? orientPage", feature);
        // Order matters: render → first OCR → orient → retry OCR. An upright page names itself
        // on the first OCR and never reaches the orient call.
        int render = feature.IndexOf("RenderFirstPageAsync", StringComparison.Ordinal);
        int firstOcr = feature.IndexOf("RecognizeBoundedOrDefaultAsync", StringComparison.Ordinal);
        int orient = feature.IndexOf("_orientPage(page)", StringComparison.Ordinal);
        int retryOcr = feature.IndexOf("RecognizeBoundedOrDefaultAsync", firstOcr + 1, StringComparison.Ordinal);
        Assert.True(render >= 0 && firstOcr > render && orient > firstOcr && retryOcr > orient,
            "WatermarkFeature.TryAutoName must OCR as-is first, orient only when starved, then OCR again.");

        string main = Read("src", "WatermarkRemover.App", "ViewModels", "MainViewModel.cs");
        Assert.Contains("orientPage: image => new PdfOrientationDetector(SharedOcrEngine).OrientAsync(image)", main);
    }

    private static string FindSolutionRoot()
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(PdfOrientationDetectorTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WatermarkRemover.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static byte[] Png(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A genuinely blank sheet: white ink-free pixels, unlike the transparent test PNG.</summary>
    private static byte[] WhitePng(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Returns a scripted line per call — the detector queries 0°, 180° first and
    /// only then 90°/270° when the vertical pair is ambiguous, so the Nth call is the Nth
    /// queried candidate.</summary>
    private sealed class ScriptedOcr(params int[] charsPerTurn) : IOcrEngine
    {
        public string DisplayName => "Scripted";

        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            int turn = Calls++;
            string text = new('字', turn < charsPerTurn.Length ? charsPerTurn[turn] : 0);
            IReadOnlyList<string> lines = text.Length == 0
                ? Array.Empty<string>()
                : new[] { text };
            return Task.FromResult(lines);
        }
    }

    private sealed class UnusableOcr : IOcrEngine
    {
        public string DisplayName => "Unusable";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            throw new InvalidOperationException("本机没有可用的 OCR 引擎");
    }

    // ---------- the voting rule (pure, no OCR / image work) ----------

    [Fact]
    public void Upright_page_with_clear_majority_reports_no_turn()
    {
        var scores = new Dictionary<int, double> { [0] = 500, [1] = 12, [2] = 8, [3] = 10 };
        Assert.Equal(0, PdfOrientationDetector.PickBestTurn(scores, minWinningScore: 20));
    }

    [Fact]
    public void Upside_down_page_votes_for_two_turns()
    {
        var scores = new Dictionary<int, double> { [0] = 9, [1] = 11, [2] = 480, [3] = 14 };
        Assert.Equal(2, PdfOrientationDetector.PickBestTurn(scores, minWinningScore: 20));
    }

    [Fact]
    public void Sideways_page_votes_for_the_quarter_turn_that_reads()
    {
        var scores = new Dictionary<int, double> { [0] = 7, [1] = 12, [2] = 10, [3] = 390 };
        Assert.Equal(3, PdfOrientationDetector.PickBestTurn(scores, minWinningScore: 20));
    }

    [Fact]
    public void A_marginal_difference_never_turns_the_file()
    {
        // 580 vs 500 is inside the 1.25 win factor — OCR noise on an upright page.
        var scores = new Dictionary<int, double> { [0] = 500, [1] = 8, [2] = 580, [3] = 9 };
        Assert.Equal(0, PdfOrientationDetector.PickBestTurn(scores, minWinningScore: 20));
    }

    [Fact]
    public void Blank_pages_stay_put_even_when_a_turn_scores_higher()
    {
        // 15 characters everywhere: nothing decisive — never rotate on whispers.
        var scores = new Dictionary<int, double> { [0] = 2, [1] = 15, [2] = 3, [3] = 1 };
        Assert.Equal(0, PdfOrientationDetector.PickBestTurn(scores, minWinningScore: 20));
    }

    // ---------- the geometric rule (line boxes, immune to rotation-robust engines) ----------

    /// <summary>Horizontal text lines, tops descending in reading order (an upright page).</summary>
    private static IReadOnlyList<RecognizedLine> UprightLines(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new RecognizedLine($"第{i}行", new RectD(0.1, 0.1 + i * 0.08, 0.8, 0.04)))
            .ToList();

    [Fact]
    public void Geometry_reads_an_upright_page_high()
    {
        Assert.True(PdfOrientationDetector.GeometryScore(UprightLines(10)) >= 0.9);
    }

    [Fact]
    public void Geometry_reads_an_upside_down_page_low()
    {
        // Same boxes, but the reading order now runs bottom-up: 180°.
        var flipped = UprightLines(10).Reverse().ToList();
        Assert.True(PdfOrientationDetector.GeometryScore(flipped) <= 0.1);
    }

    [Fact]
    public void Geometry_reads_a_sideways_page_low()
    {
        // Tall narrow boxes stacked left-to-right: the page was turned a quarter way.
        var sideways = Enumerable.Range(0, 10)
            .Select(i => new RecognizedLine($"第{i}行", new RectD(0.05 + i * 0.09, 0.1, 0.03, 0.8)))
            .ToList();
        Assert.True(PdfOrientationDetector.GeometryScore(sideways) <= 0.1);
    }

    [Fact]
    public void Geometry_needs_enough_lines_to_speak()
    {
        Assert.Equal(0, PdfOrientationDetector.GeometryScore(UprightLines(2)));
    }

    [Fact]
    public async Task Bounded_engine_detection_uses_geometry_and_finds_the_upside_down_page()
    {
        // The engine reads every rotation equally well (WeChat-like) but always reports
        // upright-style boxes for the candidate it was given — the detector rotates the IMAGE,
        // so the true turn is where the returned layout looks upright. Portrait canvas: the
        // landscape guard only exempts pages wider than tall.
        var ocr = new LayoutScriptedOcr();
        var detector = new PdfOrientationDetector(ocr);

        int? steps = await detector.DetectClockwiseStepsAsync(Png(30, 40));

        // The 0°/180° pair settles it — the sideways probes never even run.
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(2, steps);
    }

    [Fact]
    public async Task A_landscape_page_is_never_turned_regardless_of_geometry()
    {
        // 表格型横版文件（征信/流水）的竖列识别框会把几何通道骗成 "sideways" — the landscape
        // guard (v1.15 bug A) must return before any OCR, whatever the engine would vote.
        var ocr = new LayoutScriptedOcr();
        var detector = new PdfOrientationDetector(ocr);

        int? steps = await detector.DetectClockwiseStepsAsync(Png(40, 30));

        Assert.Equal(0, ocr.Calls); // guarded out before any recognition
        Assert.Equal(0, steps);
    }

    /// <summary>Always answers with an upright-looking layout for the flipped (180°) candidate
    /// and nothing usable for the others — geometry must pick turn 2 despite identical text
    /// volume everywhere, and the decisive vertical pair must end the vote early.</summary>
    private sealed class LayoutScriptedOcr : IBoundedOcrEngine
    {
        public string DisplayName => "LayoutScripted";

        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            RecognizeWithBoundsAsync(imageBytes).ContinueWith(t =>
                (IReadOnlyList<string>)t.Result.Select(l => l.Text).ToList());

        public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
        {
            int turn = Calls++;
            IReadOnlyList<RecognizedLine> lines = turn == 1
                ? UprightLines(8)
                : Array.Empty<RecognizedLine>();
            return Task.FromResult(lines);
        }
    }

    // ---------- the orchestration (real image rotation, scripted OCR) ----------

    [Fact]
    public async Task An_upside_down_page_settles_in_two_calls_and_skips_the_sideways_probes()
    {
        // Call order is 0°, 180°, then 90°/270° only when ambiguous. ScriptedOcr indexes by
        // call: straight reads 3 chars, the flipped candidate reads 30 — decisive, so the
        // sideways probes are never asked.
        var ocr = new ScriptedOcr(3, 30);
        var detector = new PdfOrientationDetector(ocr);

        int? steps = await detector.DetectClockwiseStepsAsync(Png(30, 40));

        Assert.Equal(2, ocr.Calls);
        Assert.Equal(2, steps); // 180° was the decisive reading
    }

    [Fact]
    public async Task An_ambiguous_vertical_pair_pays_for_the_full_four_way_vote()
    {
        // 0° reads 3 chars, 180° reads 10 — under the 20-char floor, so the sideways
        // candidates are probed and 90° wins. Call order: 0°, 180°, 90°, 270° → 3, 10, 25, 2.
        var ocr = new ScriptedOcr(3, 10, 25, 2);
        var detector = new PdfOrientationDetector(ocr);

        int? steps = await detector.DetectClockwiseStepsAsync(Png(30, 40));

        Assert.Equal(4, ocr.Calls);
        Assert.Equal(1, steps);
    }

    [Fact]
    public async Task A_blank_page_is_never_voted_on()
    {
        // Two 30-char readings would make the unguarded detector spend four OCR passes and
        // return 0 anyway. A blank sheet has no direction, so the pixel verdict ends it first.
        var ocr = new ScriptedOcr(30, 30);
        var detector = new PdfOrientationDetector(ocr);

        int? steps = await detector.DetectClockwiseStepsAsync(WhitePng(30, 40));

        Assert.Equal(0, steps);
        Assert.Equal(0, ocr.Calls); // not one recognition pass was paid
    }

    [Fact]
    public async Task An_unusable_engine_reports_no_verdict()
    {
        var detector = new PdfOrientationDetector(new UnusableOcr());

        int? steps = await detector.DetectClockwiseStepsAsync(Png(30, 40));

        Assert.Null(steps); // "cannot judge" must not read as "already upright"
    }
}
