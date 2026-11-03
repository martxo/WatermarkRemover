using System.Diagnostics;
using SkiaSharp;
using WatermarkRemover.Ocr;
using WatermarkRemover.Ocr.Orientation;

namespace WatermarkRemover.Tests.Ocr;

/// <summary>
/// 盲扫逐页方向裁决：保守决策层（横版守卫/置信度门槛/投影基线交叉/整份多数/短边奇偶约束）、
/// 投影几何基线、逐页 /Rotate 落地。模型缺失路径不在此测（DocOrientationClassifier.TryCreate
/// 返回 false，扫描行为与 P1 整份同转一致，由 ScanCapturePipelineTests 覆盖）。
/// </summary>
public class PerPageOrientationTests
{
    private static PerPageOrientationDecider.PageVote Vote(
        int? turns, float conf = 0.95f, bool? baseline = null, bool back = false, bool landscape = false) =>
        new(ClassifierTurns: turns, ClassifierConfidence: conf, BaselineIsHorizontal: baseline,
            IsBackSide: back, IsLandscape: landscape);

    // ---------- 逐页独立票 ----------

    [Fact]
    public void High_confidence_votes_decide_each_page_independently()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(0), Vote(2, baseline: true), Vote(1, baseline: false), Vote(3, baseline: false),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(new[] { 0, 2, 1, 3 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, s));
    }

    [Fact]
    public void Low_confidence_votes_abstain_instead_of_turning()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(2, conf: 0.60f), Vote(0, conf: 0.70f),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(new[] { 0, 0 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Undecided, s));
    }

    [Fact]
    public void A_baseline_disagreement_abstains_the_page()
    {
        // 分类器说竖排（90°）而投影基线说横排 → 矛盾，弃权。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(1, baseline: true),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(0, decision.Turns[0]);
        Assert.Equal(PerPageOrientationDecider.Provenance.Undecided, decision.Sources[0]);
    }

    [Fact]
    public void A_baseline_agreement_keeps_the_classifier_vote()
    {
        // 分类器 90°（竖排）+ 基线竖排 → 一致，采纳。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(1, baseline: false),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(1, decision.Turns[0]);
        Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, decision.Sources[0]);
    }

    [Fact]
    public void A_missing_baseline_never_blocks_the_classifier()
    {
        // 基线弃权（低对比/近白）不拦分类器：交叉校验只在「明确矛盾」时生效。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(2, baseline: null),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(2, decision.Turns[0]);
    }

    // ---------- 横版守卫（表格页：征信/流水） ----------

    [Fact]
    public void Landscape_pages_are_never_turned_even_with_a_confident_vote()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(1, landscape: true), Vote(0),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(0, decision.Turns[0]);
        Assert.Equal(PerPageOrientationDecider.Provenance.LandscapeGuard, decision.Sources[0]);
    }

    // ---------- 整份多数投票 ----------

    [Fact]
    public void An_outlier_vote_is_absorbed_by_a_strong_majority()
    {
        // 4 页 180° + 1 页误判 0°：装订阴影造成的单页误判被整份一致性吸收。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(2), Vote(2), Vote(2), Vote(2), Vote(0, conf: 0.97f),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(new[] { 2, 2, 2, 2, 2 }, decision.Turns);
        Assert.Equal(PerPageOrientationDecider.Provenance.Majority, decision.Sources[4]);
    }

    [Fact]
    public void A_weak_majority_never_overrides_page_votes()
    {
        // 4 页有票但最高票占比 0.5 < 2/3：无主导票，互不吸收。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(0), Vote(0), Vote(2), Vote(3),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(new[] { 0, 0, 2, 3 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, s));
    }

    [Fact]
    public void Fewer_than_three_votes_never_form_a_majority()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(2), Vote(2),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(new[] { 2, 2 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, s));
    }

    // ---------- 短边装订奇偶约束 ----------

    [Fact]
    public void Undecided_backs_follow_the_short_edge_parity_of_the_majority()
    {
        // 短边装订：反面物理上与正面相差 180°。正面多数 0° → 未定反面推定 2。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(0), Vote(turns: null, back: true), // 反面低置信弃权
            Vote(0), Vote(turns: null, back: true),
            Vote(0), Vote(turns: null, back: true),
        }, PerPageOrientationDecider.Binding.ShortEdge);

        Assert.Equal(new[] { 0, 2, 0, 2, 0, 2 }, decision.Turns);
        Assert.Equal(PerPageOrientationDecider.Provenance.Parity, decision.Sources[1]);
        Assert.Equal(PerPageOrientationDecider.Provenance.Parity, decision.Sources[3]);
    }

    [Fact]
    public void Long_edge_backs_inherit_the_majority_without_the_flip()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(0), Vote(turns: null, back: true),
            Vote(0), Vote(turns: null, back: true),
            Vote(0), Vote(turns: null, back: true),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, decision.Turns);
    }

    [Fact]
    public void A_decided_page_is_never_overridden_by_the_parity_constraint()
    {
        // 物理例外（混纸、插页）：已定页绝不因奇偶约束被推翻。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(0), Vote(0, back: true), // 反面明确说 0°（非短边物理预期）→ 信任页面
            Vote(0), Vote(turns: null, back: true),
            Vote(0), Vote(turns: null, back: true),
        }, PerPageOrientationDecider.Binding.ShortEdge);

        Assert.Equal(0, decision.Turns[1]);
        Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, decision.Sources[1]);
    }

    // ---------- 投影几何基线 ----------

    private static byte[] BarsPng(int width, int height, bool vertical)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            const int count = 12;
            if (vertical)
            {
                int period = width / count;
                for (int i = 0; i < count; i++)
                    canvas.DrawRect(i * period, height * 0.1f, Math.Max(2, period / 4), height * 0.8f, paint);
            }
            else
            {
                int period = height / count;
                for (int i = 0; i < count; i++)
                    canvas.DrawRect(width * 0.1f, i * period, width * 0.8f, Math.Max(2, period / 4), paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void The_baseline_reads_horizontal_bars_as_horizontal_text()
    {
        var vote = ProjectionAxisBaseline.Judge(BarsPng(256, 360, vertical: false));
        Assert.NotNull(vote);
        Assert.True(vote!.Value.IsHorizontalText);
    }

    [Fact]
    public void The_baseline_reads_vertical_bars_as_vertical_text()
    {
        var vote = ProjectionAxisBaseline.Judge(BarsPng(256, 360, vertical: true));
        Assert.NotNull(vote);
        Assert.False(vote!.Value.IsHorizontalText);
    }

    [Fact]
    public void A_near_white_page_abstains()
    {
        var info = new SKImageInfo(256, 360, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Null(ProjectionAxisBaseline.Judge(data.ToArray()));
    }

    // ---------- 逐页 /Rotate 落地 ----------

    [Fact]
    public void Builder_writes_one_rotation_per_page()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"per-page-rot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var document = new WatermarkRemover.Scan.Core.Models.ScanDocument
            {
                Pages = Enumerable.Range(0, 3).Select(i => new WatermarkRemover.Scan.Core.Models.ScannedPage
                {
                    Image = WhiteLikeFrame(i), Index = i, SheetIndex = i,
                }).ToList(),
                Settings = new WatermarkRemover.Scan.Core.Models.ScanSettings
                {
                    Resolution = WatermarkRemover.Scan.Core.Models.ScanResolution.Dpi300,
                    Color = WatermarkRemover.Scan.Core.Models.ColorMode.BlackWhite,
                },
                BaseName = "逐页裁决",
            };
            var options = new WatermarkRemover.Pdf.PdfBuildOptions { HighCompression = true };
            var encoder = new WatermarkRemover.Scan.Engine.ScanImageEncoder();
            double dpi = WatermarkRemover.Pdf.PdfDocumentBuilder.ResolveDpi(document.Settings);
            var encoded = document.Pages
                .Select(p => WatermarkRemover.Pdf.PdfDocumentBuilder.EncodePage(p, options, encoder, dpi))
                .ToList();
            int[] turns = { 0, 1, 2 };
            byte[] pdf = WatermarkRemover.Pdf.PdfDocumentBuilder.Build(
                document, options, encoded, clockwiseQuarterTurns: 0, perPageQuarterTurns: turns);

            string path = Path.Combine(dir, "per-page.pdf");
            File.WriteAllBytes(path, pdf);

            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
            Assert.Equal(3, doc.PageCount);
            Assert.Equal(0, doc.Pages[0].Rotate);
            Assert.Equal(90, doc.Pages[1].Rotate);
            Assert.Equal(180, doc.Pages[2].Rotate);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>灰色非双纯帧（避开 CCITT 路径也能逐页旋转——两编码路径同受 /Rotate 管）。</summary>
    private static WatermarkRemover.Scan.Core.Models.RasterImage WhiteLikeFrame(int seed)
    {
        const int w = 60, h = 80;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            var o = i * 4;
            bgra[o] = 200;
            bgra[o + 1] = 200;
            bgra[o + 2] = (byte)(190 + seed);
            bgra[o + 3] = 255;
        }
        return new WatermarkRemover.Scan.Core.Models.RasterImage(w, h, bgra);
    }

    // ---------- 评测夹具（分层样本集 + 混淆矩阵） ----------

    /// <summary>
    /// 上线评测与回归（模型在位时自动执行，缺失时空转通过并提示）：
    /// 样本目录 <c>eval/orientation/&lt;layer&gt;/&lt;label&gt;/*.png</c>，label ∈ 0/90/180/270
    /// （= 该图顺时针需转的角度真值），layer 为分层名（黑白/彩色/表格页/低对比/底纹/装订阴影/
    /// 空白/横竖混排/整份倒置/短边交替）。输出混淆矩阵、分层准确率与每页耗时。
    /// 放入模型 Models\PP-LCNet_x1_0_doc_ori.onnx 与样本后运行：
    /// dotnet test --filter FullyQualifiedName~PageOrientationEvaluationTests
    /// </summary>
    [Fact]
    public async Task Layered_evaluation_harness()
    {
        if (!DocOrientationClassifier.TryCreate(out DocOrientationClassifier? classifier, out string reason))
        {
            // 模型未放置：夹具空转通过（CI 不因缺少 7MB 二进制而红）。真正的验收在模型与
            // 样本就位后跑这一条——Console 输出混淆矩阵与分层统计供人工审读。
            Console.WriteLine($"[方向评测] 跳过：{reason}。" +
                              "放入 Models\\PP-LCNet_x1_0_doc_ori.onnx 与 eval/orientation 样本后重跑。");
            return;
        }

        using var _ = classifier;
        string samplesRoot = FindEvaluationRoot();
        Assert.True(Directory.Exists(samplesRoot), $"样本目录缺失：{samplesRoot}");

        var confusion = new Dictionary<(int Truth, int Predicted), int>();
        var layerStats = new Dictionary<string, (int Total, int Correct)>();
        long totalMs = 0;
        int total = 0;

        foreach (string layerDir in Directory.GetDirectories(samplesRoot))
        {
            string layer = Path.GetFileName(layerDir);
            foreach (string labelDir in Directory.GetDirectories(layerDir))
            {
                if (!int.TryParse(Path.GetFileName(labelDir), out int truth)) continue;
                foreach (string png in Directory.EnumerateFiles(labelDir, "*.png"))
                {
                    long start = Stopwatch.GetTimestamp();
                    DocOrientationClassifier.Vote vote = classifier!.Classify(await File.ReadAllBytesAsync(png));
                    totalMs += (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    total++;

                    confusion[(truth, vote.QuarterTurns)] =
                        confusion.GetValueOrDefault((truth, vote.QuarterTurns)) + 1;
                    bool correct = vote.QuarterTurns == truth;
                    (int t, int c) = layerStats.GetValueOrDefault(layer);
                    layerStats[layer] = (t + 1, c + (correct ? 1 : 0));
                }
            }
        }

        Assert.True(total > 0, "样本集为空：请按 README 分层放入 0/90/180/270 四类样本。");

        var output = new List<string> { $"样本 {total} 张，平均 {totalMs / (double)total:0.0} ms/页", "混淆矩阵（行=真值 顺时针角度，列=预测）：" };
        var labels = new[] { 0, 1, 2, 3 };
        output.Add("      " + string.Join("  ", labels.Select(p => $"预{p * 90,3}")));
        foreach (int truth in labels)
        {
            output.Add($"真{truth * 90,3}  " + string.Join("  ",
                labels.Select(p => $"{confusion.GetValueOrDefault((truth, p)),6}")));
        }
        foreach ((string layer, (int t, int c)) in layerStats)
            output.Add($"层[{layer}]：{c}/{t} = {c / (double)t:P2}");
        Console.WriteLine(string.Join(Environment.NewLine, output));

        // 验收线：单页 ≥99%，平均耗时 ≤15 ms（误翻率由混淆矩阵人工审读）。
        double accuracy = layerStats.Values.Sum(s => s.Correct) / (double)total;
        Assert.True(accuracy >= 0.99, $"整页分类准确率 {accuracy:P2} 低于验收线 99%");
        Assert.True(totalMs / (double)total <= 15.0, "单页平均耗时超过 15 ms");
    }

    private static string FindEvaluationRoot()
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(PerPageOrientationTests).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "orientation")))
            dir = dir.Parent;
        return dir is null ? Path.Combine("eval", "orientation") : Path.Combine(dir.FullName, "eval", "orientation");
    }
}
