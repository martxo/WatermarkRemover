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
    /// （= 该图需要顺时针旋转的角度真值），layer 为分层名（黑白/彩色/表格页/低对比/底纹/
    /// 装订阴影/空白/横竖混排/整份倒置/短边交替）。
    /// 统计口径 = 保守决策层的最终输出：高置信票翻、低置信/基线矛盾/横版守卫不翻——
    /// 误翻（翻错方向）与漏翻（该翻没翻）分开计，验收线只钉误翻 ≤0.2%；漏翻是保守
    /// 策略的预期代价，按层报告供人工评估。输出混淆矩阵、分层统计与每页耗时。
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
        var layerStats = new Dictionary<string, (int Total, int Correct, int Missed, int Misrotated)>();
        var output = new List<string>();
        // conf 分带：0.80-0.85 / 0.85-0.90 / ≥0.90（票被采纳且未守卫的样本）。
        var Bands = new Dictionary<string, (int Total, int Correct, int Misrotated)>
        {
            ["0.90+"] = (0, 0, 0),
            ["0.85-0.90"] = (0, 0, 0),
            ["0.80-0.85"] = (0, 0, 0),
            ["0.50-0.80"] = (0, 0, 0),
            ["<0.50"] = (0, 0, 0),
        };
        long totalMs = 0;
        int total = 0;

        // ORT 首帧预热（图优化/内存池），不计入统计——否则首样本摊进均值会让耗时数字
        // 在「单跑 4ms / 全量套件 40ms+」之间漂移；目标线 15ms 看报告，硬断言只防灾难回归。
        string warmup = Directory.EnumerateFiles(samplesRoot, "*.png", SearchOption.AllDirectories).FirstOrDefault();
        if (warmup is not null)
        {
            byte[] warm = await File.ReadAllBytesAsync(warmup);
            classifier!.Classify(warm);
            ProjectionAxisBaseline.Judge(warm);
        }

        foreach (string layerDir in Directory.GetDirectories(samplesRoot))
        {
            string layer = Path.GetFileName(layerDir);
            if (layer == "calibration") continue;
            foreach (string labelDir in Directory.GetDirectories(layerDir))
            {
                if (!int.TryParse(Path.GetFileName(labelDir), out int truth)) continue;
                foreach (string png in Directory.EnumerateFiles(labelDir, "*.png"))
                {
                    // 生产等效输入：扫描链路里分类器吃的是 256px 探针（Probe256），不是原尺寸帧。
                    byte[] bytes = await File.ReadAllBytesAsync(png);
                    using var decoded = SKBitmap.Decode(bytes);
                    Assert.NotNull(decoded);
                    using var probe256 = DownscaleToProbe(decoded!, 256);
                    byte[] probeBytes = EncodePng(probe256);
                    long start = Stopwatch.GetTimestamp();
                    DocOrientationClassifier.Vote vote = classifier!.Classify(probeBytes);
                    bool? baseline = ProjectionAxisBaseline.Judge(probeBytes)?.IsHorizontalText;
                    bool landscape = decoded!.Width > decoded.Height;
                    PerPageOrientationDecider.Decision decision = PerPageOrientationDecider.Decide(
                        new[]
                        {
                            new PerPageOrientationDecider.PageVote(
                                vote.QuarterTurns, vote.Confidence, baseline,
                                IsBackSide: false, IsLandscape: landscape),
                        },
                        PerPageOrientationDecider.Binding.None);
                    int finalTurns = decision.Turns[0];
                    totalMs += (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    total++;

                    confusion[(truth, vote.QuarterTurns)] =
                        confusion.GetValueOrDefault((truth, vote.QuarterTurns)) + 1;
                    bool correct = finalTurns == truth;
                    bool misrotated = !correct && finalTurns != 0;
                    bool missed = !correct && finalTurns == 0 && truth != 0;
                    // 门槛调参依据：竖版（无横版守卫）样本按裸分类器票分带——把门槛移到任一带
                    // 下限时该带的正确/误翻就是当时的取舍。
                    if (!landscape)
                    {
                        string band = vote.Confidence >= 0.90 ? "0.90+"
                            : vote.Confidence >= 0.85 ? "0.85-0.90"
                            : vote.Confidence >= 0.80 ? "0.80-0.85"
                            : vote.Confidence >= 0.50 ? "0.50-0.80" : "<0.50";
                        if (!Bands.TryGetValue(band, out var bucket)) bucket = (0, 0, 0);
                        Bands[band] = (bucket.Total + 1, bucket.Correct + (vote.QuarterTurns == truth ? 1 : 0),
                            bucket.Misrotated + (vote.QuarterTurns != truth ? 1 : 0));
                    }
                    if (misrotated)
                    {
                        string detail =
                            $"[误翻样本] {Path.GetFileName(png)} 真值={truth * 90}° 判={finalTurns * 90}° " +
                            $"conf={vote.Confidence:0.000} 基线={(baseline is null ? "弃权" : baseline.Value ? "横排" : "竖排")} " +
                            $"画布={(landscape ? "横版" : "竖版")}（256px 探针）";
                        output.Add(detail);
                    }
                    (int t, int c, int m, int r) = layerStats.GetValueOrDefault(layer);
                    layerStats[layer] = (t + 1, c + (correct ? 1 : 0), m + (missed ? 1 : 0), r + (misrotated ? 1 : 0));
                }
            }
        }

        Assert.True(total > 0, "样本集为空：请按 README 分层放入 0/90/180/270 四类样本。");

        output.InsertRange(0, new[]
        {
            $"样本 {total} 张，平均 {totalMs / (double)total:0.0} ms/页（含基线与决策层）",
            "裸分类器混淆矩阵（行=真值 需转顺时针角度，列=分类原始类别）:",
        });
        var labels = new[] { 0, 1, 2, 3 };
        output.Add("       " + string.Join("  ", labels.Select(p => $"类{p * 90,3}")));
        foreach (int truth in labels)
        {
            output.Add($"需转{truth * 90,3} " + string.Join("  ",
                labels.Select(p => $"{confusion.GetValueOrDefault((truth, p)),6}")));
        }
        foreach ((string layer, (int t, int c, int m, int r)) in layerStats)
            output.Add($"层[{layer}]：正确 {c}/{t} = {c / (double)t:P2}，漏翻 {m}，误翻 {r}");

        // 置信度分带统计（门槛调参依据）：各带内「分类器票被采纳」样本的正确/误翻分布。
        foreach ((string band, var bucket) in Bands.OrderBy(kv => kv.Key))
            output.Add($"conf[{band}]：采纳 {bucket.Total}，正确 {bucket.Correct}，误翻 {bucket.Misrotated}");
        Console.WriteLine(string.Join(Environment.NewLine, output));

        // 验收线：误翻 ≤0.2%——仅在样本层 ≥3（来源多样，足以代表真实混合文档流）时启用硬
        // 断言；单一来源样本集（如仅一份 2-up 拼版件）是分类器的 OOD 场景，任何阈值都验不
        // 出真实误翻率，此时只报告数字。耗时目标 ≤15 ms/页（报告呈现），硬断言 50 ms 只防
        // 灾难性回归——全量套件并发运行时的 CPU 竞争会让均值波动，15ms 硬断会 flaky。
        int misrotatedTotal = layerStats.Values.Sum(s => s.Misrotated);
        string report = string.Join(Environment.NewLine, output);
        bool sampleSetIsDiverse = layerStats.Count >= 3;
        if (sampleSetIsDiverse)
        {
            Assert.True(misrotatedTotal / (double)total <= 0.002,
                $"误翻率 {misrotatedTotal}/{total} 超过验收线 0.2%{Environment.NewLine}{report}");
        }
        else
        {
            output.Add($"[验收挂起] 样本仅 {layerStats.Count} 层（单一来源）：误翻 {misrotatedTotal}/{total}。" +
                       "补齐 3 层以上多样真实样本后自动启用 0.2% 验收断言。");
            Console.WriteLine(string.Join(Environment.NewLine, output));
        }
        Assert.True(totalMs / (double)total <= 50.0,
            $"单页平均耗时 {totalMs / (double)total:0.0} ms 超过 50 ms 防线（目标 15 ms）{Environment.NewLine}{report}");
    }

    // ---------- 奇偶自洽采纳（真机校准 2026-11-03 黄小婷件） ----------

    /// <summary>真机复现：长边双面 5 页全歪（正 3 票需转 270、反 2 票需转 90），conf 0.55-0.86
    /// 全被 0.85 门槛挡下、3:2 形不成多数——奇偶自洽（两组相差恰 2）应让全部页面被采纳摆正。</summary>
    [Fact]
    public void Parity_consistent_low_confidence_votes_are_adopted_on_duplex_scans()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            // 页位交替 front/back；正面 conf 偏低（0.55-0.71），背面更高（0.62-0.85）。
            Vote(3, conf: 0.55f),                       // front → 需转 270
            Vote(1, conf: 0.62f, back: true),           // back  → 需转 90
            Vote(3, conf: 0.71f),                       // front
            Vote(1, conf: 0.85f, back: true),           // back（压线）
            Vote(3, conf: 0.65f),                       // front
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 3, 1, 3, 1, 3 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Parity, s));
    }

    [Fact]
    public void A_noisy_vote_breaks_parity_adoption()
    {
        // 背面组内一张杂音票 → 组不一致 → 奇偶自洽不启用：低置信票保持弃权；那张杂音票
        // conf 0.90 自身过门槛按既有单页规则独立采纳（互不牵连）。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.55f),
            Vote(1, conf: 0.62f, back: true),
            Vote(3, conf: 0.71f),
            Vote(2, conf: 0.90f, back: true),  // 背面组内杂音（自身高置信）
            Vote(3, conf: 0.65f),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 0, 0, 0, 2, 0 }, decision.Turns);
        Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, decision.Sources[3]);
        Assert.Equal(PerPageOrientationDecider.Provenance.Undecided, decision.Sources[0]);
    }

    [Fact]
    public void A_front_only_uniform_document_is_also_adopted()
    {
        // 4c 不要求正反成对：单面/纯正面文档 ≥3 页同向零杂音同样是整份歪斜的组合证据。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.55f), Vote(3, conf: 0.60f), Vote(3, conf: 0.65f),
        }, PerPageOrientationDecider.Binding.None);

        Assert.Equal(new[] { 3, 3, 3 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Parity, s));
    }

    [Fact]
    public void Parity_adoption_requires_two_votes_per_side()
    {
        // 正反各只有 1 票：组合证据太弱，不启用。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.55f), Vote(1, conf: 0.60f, back: true),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 0, 0 }, decision.Turns);
    }

    // ---------- 同向零杂音采纳（真机 1.16.85 票面 2026-11-03 18:15） ----------

    /// <summary>真机 1:1 票面：LongEdge 双面 5 页原始票全部同向 3（该机型正反同向出纸）、
    /// conf 0.572-0.779 全在 0.85 门槛下、基线全弃权、1 页空白背面已被过滤——同向零杂音
    /// 采纳应让全部内容页摆正。</summary>
    [Fact]
    public void The_rescan_vote_pattern_is_adopted_as_uniform()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.645f), Vote(3, conf: 0.779f, back: true),
            Vote(3, conf: 0.606f), Vote(3, conf: 0.572f, back: true),
            Vote(3, conf: 0.728f),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 3, 3, 3, 3, 3 }, decision.Turns);
        Assert.All(decision.Sources, s => Assert.Equal(PerPageOrientationDecider.Provenance.Parity, s));
    }

    [Fact]
    public void Sub_floor_votes_never_form_a_uniform_adoption()
    {
        // conf < 0.5 的票不算票（<0.50 带实测 1 对 11 错）：有效票只剩 2 张（<3）→ 不触发；
        // 其余两票 conf 0.606/0.572 仍低于 0.85 门槛 → 全部弃权。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.45f), Vote(3, conf: 0.40f, back: true),
            Vote(3, conf: 0.606f), Vote(3, conf: 0.572f, back: true),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 0, 0, 0, 0 }, decision.Turns);
    }

    [Fact]
    public void A_conflicting_direction_breaks_uniform_adoption()
    {
        // 4 票同向 + 1 票异向（conf 过门槛）→ 杂音破坏零杂音前提；
        // 异向高置信票按既有单页规则独立成立，多数不足 2/3 → 其余弃权。
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.645f), Vote(3, conf: 0.779f, back: true),
            Vote(3, conf: 0.606f), Vote(1, conf: 0.90f, back: true),
            Vote(3, conf: 0.728f),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 0, 0, 0, 1, 0 }, decision.Turns);
        Assert.Equal(PerPageOrientationDecider.Provenance.Classifier, decision.Sources[3]);
    }

    [Fact]
    public void Fewer_than_three_uniform_votes_do_not_trigger()
    {
        var decision = PerPageOrientationDecider.Decide(new[]
        {
            Vote(3, conf: 0.645f), Vote(3, conf: 0.779f, back: true),
        }, PerPageOrientationDecider.Binding.LongEdge);

        Assert.Equal(new[] { 0, 0 }, decision.Turns);
    }

    // ---------- 模型映射校准（换模型/再导出必须重跑） ----------

    /// <summary>Parse 纯函数：输出已是概率分布（不再 softmax），类别=当前朝向，需转=(4−类)%4。</summary>
    [Fact]
    public void Parse_takes_probability_directly_and_returns_the_complementary_turns()
    {
        // 网络实测输出形态（官方 180° demo 图）：概率分布，top=类2（当前 180°）。
        var vote = DocOrientationClassifier.Parse(new[] { 0.041f, 0.040f, 0.881f, 0.037f });
        Assert.Equal(2, vote.QuarterTurns); // 需转 180°（当前朝向的补角）
        Assert.Equal(0.881f, vote.Confidence, precision: 3); // 直接取概率，未被 softmax 压平

        // 正向页（top=类0）→ 需转 0；顺时针歪 90° 的页（top=类1）→ 需转 270°。
        Assert.Equal(0, DocOrientationClassifier.Parse(new[] { 0.9f, 0.04f, 0.03f, 0.03f }).QuarterTurns);
        Assert.Equal(3, DocOrientationClassifier.Parse(new[] { 0.04f, 0.9f, 0.03f, 0.03f }).QuarterTurns);
        Assert.Equal(1, DocOrientationClassifier.Parse(new[] { 0.03f, 0.04f, 0.04f, 0.89f }).QuarterTurns);
    }

    /// <summary>
    /// 真实模型锚定（模型在位时执行）：PaddleX 官方 doc_ori demo 图 img_rot180_demo.jpg
    /// 是一张倒置 180° 的文档照——「当前朝向」语义下 top 必须是类 2，且换算出的需转=2。
    /// 若换成语义相反的导出（top=类0），这里立即红——系统性镜像误翻在这一关被拦住。
    /// </summary>
    [Fact]
    public async Task The_real_model_reads_the_official_rot180_demo_as_currently_180()
    {
        if (!DocOrientationClassifier.TryCreate(out DocOrientationClassifier? classifier, out string reason))
        {
            Console.WriteLine($"[映射校准] 跳过：{reason}");
            return;
        }

        using var _ = classifier;
        string demo = Path.Combine(FindEvaluationRoot(), "calibration", "img_rot180_demo.jpg");
        if (!File.Exists(demo))
        {
            Console.WriteLine("[映射校准] 跳过：缺校准图 eval/orientation/calibration/img_rot180_demo.jpg");
            return;
        }

        DocOrientationClassifier.Vote vote = classifier.Classify(await File.ReadAllBytesAsync(demo));
        Assert.True(vote.Confidence >= 0.6, $"demo 图置信度异常偏低：{vote.Confidence:0.000}");
        Assert.Equal(2, vote.QuarterTurns);
    }

    private static SKBitmap DownscaleToProbe(SKBitmap source, int maxEdge)
    {
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maxEdge) return source;
        double factor = maxEdge / (double)longest;
        var target = new SKBitmap(
            (int)Math.Round(source.Width * factor), (int)Math.Round(source.Height * factor),
            source.ColorType, source.AlphaType);
        source.ScalePixels(target, new SKSamplingOptions(SKFilterMode.Linear));
        return target;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static string FindEvaluationRoot()
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(PerPageOrientationTests).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "orientation")))
            dir = dir.Parent;
        return dir is null ? Path.Combine("eval", "orientation") : Path.Combine(dir.FullName, "eval", "orientation");
    }
}
