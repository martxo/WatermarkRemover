using SkiaSharp;
using WatermarkRemover.App.Naming;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;
using Xunit;

namespace WatermarkRemover.Tests.App.Naming;

/// <summary>
/// Fixed-region credit report naming: the「被查询者姓名」label box is found in the top band
/// and the subject's name is read from the cell directly below it — never from full-page
/// OCR text order. Geometry constants are derived from a real report's recognition report
/// (label X=0.0716 Y=0.2728 W=0.0427 H=0.0125; name 庞结 X=0.0851 Y=0.3015; next column
/// 身份证 X=0.1477).
/// </summary>
public class CreditReportNameLocatorTests
{
    // --- anchor matching ---

    [Theory]
    [InlineData("被查询者姓名", true)]
    [InlineData("被查询者姓名 证件类型", true)] // merged next cell
    [InlineData("被 查询者姓名", true)]          // OCR-inserted spaces
    [InlineData("被查询者证件类型", false)]
    [InlineData("查询机构", false)]
    [InlineData("姓名", false)]                  // 本人版 label — never matched by the specific check
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Anchor_label_matching_tolerates_ocr_noise(string? text, bool expected) =>
        Assert.Equal(expected, CreditReportNameLocator.IsAnchorLabel(text));

    [Fact]
    public void A_bare_name_label_only_anchors_on_a_personal_report()
    {
        var nameLine = new RecognizedLine("姓名:李言波", new RectD(0.07, 0.30, 0.1, 0.02));

        // 本人版 personal report: the bare 姓名 label is the anchor.
        var personal = new[]
        {
            new RecognizedLine("个人信用报告", new RectD(0.20, 0.20, 0.20, 0.03)),
            nameLine,
        };
        Assert.Equal("姓名:李言波", CreditReportNameLocator.FindAnchor(personal)!.Text);

        // An ID card's own 姓名 row must not borrow the credit-report suffix.
        var idCard = new[]
        {
            new RecognizedLine("居民身份证", new RectD(0.10, 0.10, 0.20, 0.03)),
            nameLine,
        };
        Assert.Null(CreditReportNameLocator.FindAnchor(idCard));

        // The enterprise edition keeps its placeholder stem: even with a 征信中心 marker,
        // its plain 姓名 cell must not become the name.
        var enterprise = new[]
        {
            new RecognizedLine("企业信用报告", new RectD(0.20, 0.20, 0.20, 0.03)),
            new RecognizedLine("征信中心", new RectD(0.10, 0.10, 0.10, 0.02)),
            nameLine,
        };
        Assert.Null(CreditReportNameLocator.FindAnchor(enterprise));
    }

    [Fact]
    public async Task The_self_service_edition_reads_the_value_on_the_label_line()
    {
        // Field scan 2026-10-27 11:21: 本人版 header OCR'd as「姓名:李言波」— no cell below it,
        // so the geometric crop could never produce a name and the file kept its timestamp name.
        var lines = new[]
        {
            new RecognizedLine("个人信用报告", new RectD(0.20, 0.20, 0.20, 0.03)),
            new RecognizedLine("告编号:2026102216255204990171", new RectD(0.10, 0.22, 0.30, 0.02)),
            new RecognizedLine("姓名:李言波", new RectD(0.07, 0.27, 0.12, 0.02)),
        };
        var engine = new CellOnlyOcr();

        string? name = await CreditReportNameLocator.TryReadFromLinesAsync(WhitePage(), lines, engine);

        Assert.Equal("李言波", name);
        Assert.Equal(0, engine.Calls); // value came from the label line — no cell crop OCR
    }

    [Fact]
    public async Task The_band_probe_reads_the_self_service_name_without_a_cell_crop()
    {
        var engine = new FakeBoundedEngine
        {
            BoundedResult = new[]
            {
                new RecognizedLine("个人信用报告", new RectD(0.20, 0.20, 0.20, 0.03)),
                new RecognizedLine("姓名:李言波", new RectD(0.07, 0.27, 0.12, 0.02)),
            },
            CellResult = new[] { "不该被用到" },
        };

        string? name = await CreditReportNameLocator.TryLocateNameAsync(WhitePng(400, 600), engine);

        Assert.Equal("李言波", name);
        Assert.Equal(1, engine.WithBoundsCalls);
        Assert.Equal(0, engine.RecognizeCalls);
    }

    [Fact]
    public void The_topmost_leftmost_anchor_wins_on_a_multi_up_sheet()
    {
        var lines = new[]
        {
            new RecognizedLine("被查询者姓名", new RectD(0.5, 0.6, 0.1, 0.02)),
            new RecognizedLine("被查询者姓名", new RectD(0.07, 0.6, 0.1, 0.02)),
            new RecognizedLine("被查询者姓名", new RectD(0.08, 0.3, 0.1, 0.02)),
        };

        Assert.Equal(0.08, CreditReportNameLocator.FindAnchor(lines)!.Bounds!.Value.X);
    }

    // --- geometry ---

    [Fact]
    public void The_cell_rect_covers_the_real_name_cell_but_not_the_next_column()
    {
        // Band-normalized coordinates (band = top 45% of the page → Y page / 0.45).
        var label = new RectD(0.0716, 0.2728 / 0.45, 0.0427, 0.0125 / 0.45);
        var name = new RectD(0.0851, 0.3015 / 0.45, 0.0157, 0.0101 / 0.45);
        var nextColumn = new RectD(0.1477, 0.3015 / 0.45, 0.0207, 0.0125 / 0.45);

        RectD cell = CreditReportNameLocator.CellRect(label);

        Assert.True(name.X >= cell.X && name.Right <= cell.Right, "name inside horizontally");
        Assert.True(name.Y >= cell.Y && name.Top <= cell.Top, "name inside vertically");
        Assert.True(nextColumn.X >= cell.Right, "next column (证件类型值) outside the crop");
    }

    // --- full-page OCR bounds fallback ---

    /// <summary>Returns a fixed cell reading; the locator should call it exactly once.</summary>
    private sealed class CellOnlyOcr : IOcrEngine
    {
        public int Calls { get; private set; }

        public string DisplayName => "cell";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "戴瀚锋" });
        }
    }

    private static byte[] WhitePage()
    {
        using var bitmap = new SKBitmap(400, 400);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Full_page_bounds_read_the_cell_when_the_band_probe_missed_the_label()
    {
        // Regression for the 2026-10-24 morning scan: the top-band OCR missed the label on a
        // 2-up credit report, while full-page OCR saw it with bounds.
        var lines = new[]
        {
            new RecognizedLine("被查询者姓名", new RectD(0.07, 0.27, 0.043, 0.019)),
        };
        var engine = new CellOnlyOcr();

        string? name = await CreditReportNameLocator.TryReadFromLinesAsync(WhitePage(), lines, engine);

        Assert.Equal("戴瀚锋", name);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Without_the_anchor_no_cell_crop_is_recognised()
    {
        var lines = new[] { new RecognizedLine("查询机构", new RectD(0.1, 0.1, 0.1, 0.02)) };
        var engine = new CellOnlyOcr();

        Assert.Null(await CreditReportNameLocator.TryReadFromLinesAsync(WhitePage(), lines, engine));
        Assert.Equal(0, engine.Calls);
    }

    // --- value cell text cleanup ---

    [Fact]
    public void Cell_text_yields_the_first_name_run() =>
        Assert.Equal("庞结", CreditReportNameLocator.CleanCellValue(new[] { "庞结" }));

    [Fact]
    public void A_leaked_neighbour_cell_is_skipped() =>
        Assert.Equal("庞结", CreditReportNameLocator.CleanCellValue(new[] { "庞结", "身份证" }));

    [Fact]
    public void Spaced_out_names_are_joined() =>
        Assert.Equal("陈圣诈", CreditReportNameLocator.CleanCellValue(new[] { "陈 圣 诈" }));

    [Fact]
    public void Digits_and_label_words_yield_null()
    {
        Assert.Null(CreditReportNameLocator.CleanCellValue(new[] { "450923199007295" }));
        Assert.Null(CreditReportNameLocator.CleanCellValue(new[] { "身份证" }));
        Assert.Null(CreditReportNameLocator.CleanCellValue(new[] { "被查询者" }));
        Assert.Null(CreditReportNameLocator.CleanCellValue(Array.Empty<string>()));
    }

    // --- orchestration ---

    [Fact]
    public async Task Locates_the_name_from_the_cell_below_the_label()
    {
        var engine = new FakeBoundedEngine
        {
            // Real-report geometry (band-normalized: page Y / 0.45).
            BoundedResult = new[]
            {
                new RecognizedLine("个人信用报告", new RectD(0.2208, 0.3584, 0.0705, 0.0407)),
                new RecognizedLine("被查询者姓名", new RectD(0.0716, 0.6062, 0.0427, 0.0278)),
                new RecognizedLine("被查询者证件类型", new RectD(0.1291, 0.6116, 0.0563, 0.0278)),
            },
            CellResult = new[] { "庞结" },
        };

        string? name = await CreditReportNameLocator.TryLocateNameAsync(WhitePng(400, 600), engine);

        Assert.Equal("庞结", name);
        Assert.Equal(1, engine.WithBoundsCalls); // one small band probe — never a full-page pass
        Assert.Equal(1, engine.RecognizeCalls);  // one tiny cell crop
    }

    [Fact]
    public async Task Falls_back_to_null_when_the_label_is_absent()
    {
        var engine = new FakeBoundedEngine
        {
            BoundedResult = new[] { new RecognizedLine("营业执照", new RectD(0.1, 0.1, 0.2, 0.05)) },
        };

        Assert.Null(await CreditReportNameLocator.TryLocateNameAsync(WhitePng(400, 600), engine));
        Assert.Equal(3, engine.WithBoundsCalls); // portrait canvas: 0°/90°/270° probes, bounded cost
        Assert.Equal(0, engine.RecognizeCalls); // no cell OCR without an anchor
    }

    [Fact]
    public async Task A_sideways_page_is_probed_at_90_and_still_locates_the_name()
    {
        // Real failure (v1.14.2 field scan): the ADF presented the landscape report sideways,
        // so the 0° band had no label; the 90° probe (the auto-orient direction) must find it.
        var engine = new FakeBoundedEngine
        {
            BoundedSequence = new[]
            {
                Array.Empty<RecognizedLine>(),
                new[] { new RecognizedLine("被查询者姓名", new RectD(0.0716, 0.6062, 0.0427, 0.0278)) },
            },
            CellResult = new[] { "庞结" },
        };

        string? name = await CreditReportNameLocator.TryLocateNameAsync(WhitePng(400, 600), engine);

        Assert.Equal("庞结", name);
        Assert.Equal(2, engine.WithBoundsCalls);
        Assert.Equal(1, engine.RecognizeCalls);
    }

    [Fact]
    public async Task An_upside_down_landscape_page_is_probed_at_180()
    {
        var engine = new FakeBoundedEngine
        {
            BoundedSequence = new[]
            {
                Array.Empty<RecognizedLine>(),
                new[] { new RecognizedLine("被查询者姓名", new RectD(0.0716, 0.6062, 0.0427, 0.0278)) },
            },
            CellResult = new[] { "庞结" },
        };

        string? name = await CreditReportNameLocator.TryLocateNameAsync(WhitePng(600, 400), engine);

        Assert.Equal("庞结", name);
        Assert.Equal(2, engine.WithBoundsCalls); // landscape canvas: 0°/180° only — no 90/270
        Assert.Equal(1, engine.RecognizeCalls);
    }

    [Fact]
    public async Task Skips_gracefully_without_a_bounded_engine()
    {
        var engine = new PlainEngine();

        Assert.Null(await CreditReportNameLocator.TryLocateNameAsync(WhitePng(400, 600), engine));
    }

    private static byte[] WhitePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class FakeBoundedEngine : IBoundedOcrEngine
    {
        public string DisplayName => "Fake";
        public IReadOnlyList<RecognizedLine>? BoundedResult { get; init; }

        /// <summary>Per-call responses for orientation probing (index = probe attempt); calls
        /// beyond the sequence return an empty list.</summary>
        public IReadOnlyList<IReadOnlyList<RecognizedLine>>? BoundedSequence { get; init; }
        public IReadOnlyList<string> CellResult { get; init; } = Array.Empty<string>();
        public int WithBoundsCalls;
        public int RecognizeCalls;

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
        {
            Interlocked.Increment(ref RecognizeCalls);
            return Task.FromResult(CellResult);
        }

        public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
        {
            int call = Interlocked.Increment(ref WithBoundsCalls) - 1;
            IReadOnlyList<RecognizedLine> result =
                BoundedSequence is { } sequence
                    ? call < sequence.Count ? sequence[call] : Array.Empty<RecognizedLine>()
                    : BoundedResult ?? Array.Empty<RecognizedLine>();
            return Task.FromResult(result);
        }
    }

    private sealed class PlainEngine : IOcrEngine
    {
        public string DisplayName => "Plain";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
