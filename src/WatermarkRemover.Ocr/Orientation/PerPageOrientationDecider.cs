namespace WatermarkRemover.Ocr.Orientation;

/// <summary>
/// 盲扫逐页方向裁决的保守决策层（v1.16.83）。参照 Jev 裁判机制：宁可漏翻让人手动转一页，
/// 也不要高置信误翻。输入是每页独立的票（分类器 + 投影几何基线），输出每页的顺时针
/// 四分之一转数（0-3）与判定来源——来源同时是上线后日志采样监控的挂钩。
///
/// 规则（按序）：
/// 1. 横版页（宽&gt;高）判 0：表格型文件（征信/流水）只信整页几何，绝不靠行级投票硬转；
/// 2. 分类器置信度低于门槛 → 弃权；
/// 3. 投影基线的大类（横排 vs 竖排）与分类器矛盾 → 弃权；
/// 4. 同份文档多数投票：≥3 页有票且最高票占比 ≥ 2/3 时，与多数矛盾的离群票改为多数
///   （装订阴影/贴纸压字造成的单页误判被整份一致性吸收）；
/// 5. 短边装订奇偶约束：物理上正反面朝向相差 180°——未定页的反面按「多数 + 2」推定；
///   已定页不因约束被推翻（物理例外：混纸、插页）。
/// 6. 仍未定的页保持原样（0），日志留痕。
///
/// 第二阶段扩展点：textline_ori（0.96 MB）的行级裁决可作为第三种票源接入
/// <see cref="PageOrientationVote"/>——决策层只看票，不关心票从哪来。
/// </summary>
public static class PerPageOrientationDecider
{
    /// <summary>分类器 softmax 置信度门槛：低于此值宁可弃权。</summary>
    internal const double ConfidenceGate = 0.85;

    /// <summary>多数投票所需的最少有票页数。</summary>
    internal const int MajorityMinPages = 3;

    /// <summary>最高票占比达到此份额才算「多数」。</summary>
    internal const double MajorityShare = 2.0 / 3.0;

    /// <summary>每页判定来源——日志采样按来源分层监控误翻率。</summary>
    public enum Provenance
    {
        Undecided,      // 保持原样（漏翻，可手动转）
        Classifier,     // 分类器高置信 + 基线交叉一致
        Majority,       // 离群票被整份多数吸收
        Parity,         // 短边装订奇偶推定（未定反面 = 多数 + 2）
        LandscapeGuard, // 横版页不转
    }

    /// <summary>一页的原始票源。全部可空：分类器不可用/低置信/基线弃权各自独立表达。</summary>
    public readonly record struct PageVote(
        int? ClassifierTurns,
        float ClassifierConfidence,
        bool? BaselineIsHorizontal,
        bool IsBackSide,
        bool IsLandscape);

    /// <summary>裁决结果：每页转数 + 来源（两数组按阅读序对齐）。</summary>
    public sealed record Decision(IReadOnlyList<int> Turns, IReadOnlyList<Provenance> Sources)
    {
        public static Decision AllUndecided(int pageCount)
        {
            var turns = new int[pageCount];
            var sources = Enumerable.Repeat(Provenance.Undecided, pageCount).ToArray();
            return new Decision(turns, sources);
        }
    }

    /// <summary>DuplexMode.Off/LongEdge/ShortEdge 的数值镜像，避免决策层依赖扫描工程集。</summary>
    public enum Binding
    {
        None = 0,
        LongEdge = 1,
        ShortEdge = 2,
    }

    public static Decision Decide(IReadOnlyList<PageVote> pages, Binding binding)
    {
        var turns = new int[pages.Count];
        var sources = new Provenance[pages.Count];

        // ---- 第 1-3 条：逐页独立票（横版守卫 → 置信度 → 基线交叉） ----
        for (int i = 0; i < pages.Count; i++)
        {
            PageVote vote = pages[i];
            (turns[i], sources[i]) = JudgePage(vote);
        }

        // ---- 第 4 条：整份多数投票吸收离群票（横版守卫页是内容决定的，不参与吸收） ----
        int? majority = FindMajority(turns, sources);
        if (majority is { } m)
        {
            for (int i = 0; i < turns.Length; i++)
            {
                if (sources[i] == Provenance.Classifier &&
                    ((turns[i] - m) % 4 + 4) % 4 != 0)
                {
                    turns[i] = m;
                    sources[i] = Provenance.Majority;
                }
            }
        }

        // ---- 第 4b 条：奇偶自洽采纳（真机校准 2026-11-03 黄小婷件）：长边/短边双面的正反面
        // 朝向物理上相差恰 180°——当正、反两组各持一致票且相差恰 2 时，组合本身就是方向证据，
        // 低置信票由物理约束集体背书（该案 5 票全对、conf 0.55-0.86 全被门槛挡下、3:2 票
        // 形不成多数，页面全歪落盘）。启用条件保守：正反两组各 ≥2 票组内一致、相差恰 2、
        // 且组间页位交替——孤立单票或纯正面文档不触发。
        if (majority is null)
        {
            // 原始票（含被门槛挡下的低置信票）按正/反分组；组内全一致才认；横版守卫页不参与。
            int? frontMode = UniformVote(pages, isBack: false);
            int? backMode = UniformVote(pages, isBack: true);
            int frontCount = pages.Count(p => !p.IsBackSide && !p.IsLandscape && p.ClassifierTurns is not null);
            int backCount = pages.Count(p => p.IsBackSide && !p.IsLandscape && p.ClassifierTurns is not null);
            if (frontMode is { } f && backMode is { } b &&
                frontCount >= 2 && backCount >= 2 &&
                ((f - b) % 4 + 4) % 4 == 2)
            {
                // 物理自洽成立：全部有票页按奇偶采纳，无票页按奇偶补齐。
                for (int i = 0; i < turns.Length; i++)
                {
                    if (sources[i] is Provenance.Undecided or Provenance.Classifier)
                    {
                        turns[i] = pages[i].IsBackSide ? b : f;
                        sources[i] = Provenance.Parity;
                    }
                }
            }
        }

        // ---- 第 4c 条：同向零杂音采纳（真机 1.16.85 票面 2026-11-03）：同一次放纸的整份歪斜
        // 在原始票上就是「全部同向」——该机型 LongEdge 双面正反同向出纸（5/5 票全 3、conf
        // 0.57-0.78 全在门槛下、组内零杂音），「正反差恰 2」的假设对该机型不成立。组合证据
        // （≥3 页零杂音同向）强于单页置信度。条件保守：非横版有票页 ≥3、原始票全部一致、
        // conf ≥0.5（<0.50 带实测 1 对 11 错，绝不算票）；孤立票、杂音票、横版票均不触发。
        if (majority is null && UniformVoteAll(pages) is { } uniform && UniformVoteCount(pages) >= 3)
        {
            for (int i = 0; i < turns.Length; i++)
            {
                if (sources[i] is Provenance.Undecided or Provenance.Classifier && !pages[i].IsLandscape)
                {
                    turns[i] = uniform;
                    sources[i] = Provenance.Parity;
                }
            }
        }

        // ---- 第 5 条：短边装订奇偶约束——只推定未定反面，绝不推翻已定页 ----
        if (binding == Binding.ShortEdge && majority is { } front)
        {
            int expectedBack = (front + 2) % 4;
            for (int i = 0; i < turns.Length; i++)
            {
                if (sources[i] == Provenance.Undecided && pages[i].IsBackSide)
                {
                    turns[i] = expectedBack;
                    sources[i] = Provenance.Parity;
                }
            }
        }

        return new Decision(turns, sources);
    }

    private static (int Turns, Provenance Source) JudgePage(PageVote vote)
    {
        // 横版页判 0：内容本身横排，转了反而错（表格守卫只信几何，不票决）。
        if (vote.IsLandscape) return (0, Provenance.LandscapeGuard);

        if (vote.ClassifierTurns is not { } classifierTurns ||
            vote.ClassifierConfidence < ConfidenceGate)
        {
            return (0, Provenance.Undecided);
        }

        // 基线交叉：分类器说竖排（1/3）而基线说横排（或反之）→ 弃权。
        if (vote.BaselineIsHorizontal is { } baseline &&
            baseline != (classifierTurns is 0 or 2))
        {
            return (0, Provenance.Undecided);
        }

        return (classifierTurns, Provenance.Classifier);
    }

    /// <summary>有票页中占比达到份额的最高票；票数不足或无主导票返回 null。</summary>
    internal static int? FindMajority(IReadOnlyList<int> turns, IReadOnlyList<Provenance> sources)
    {
        int voted = sources.Count(s => s == Provenance.Classifier);
        if (voted < MajorityMinPages) return null;

        var counts = new Dictionary<int, int>();
        for (int i = 0; i < turns.Count; i++)
        {
            if (sources[i] != Provenance.Classifier) continue;
            counts[turns[i]] = counts.GetValueOrDefault(turns[i]) + 1;
        }

        KeyValuePair<int, int> best = counts.MaxBy(kv => kv.Value);
        return best.Value >= Math.Ceiling(voted * MajorityShare) ? best.Key : null;
    }

    /// <summary>指定正/反侧的全部原始票（含低置信）一致时返回该票向，否则 null——
    /// 奇偶自洽采纳要求组内零杂音，一张噪声票就让整组保持弃权（宁可漏翻）。</summary>
    private static int? UniformVote(IReadOnlyList<PageVote> pages, bool isBack)
    {
        int? mode = null;
        foreach (PageVote p in pages)
        {
            if (p.IsBackSide != isBack || p.IsLandscape || p.ClassifierTurns is not { } t) continue;
            if (mode is null) mode = t;
            else if (mode != t) return null;
        }
        return mode;
    }

    /// <summary>conf 可信下限：低于此带的原始票不参与同向采纳（<0.50 带实测 1 对 11 错）。</summary>
    internal const double UniformConfFloor = 0.5;

    /// <summary>全部非横版有票页（conf ≥ floor）票向一致时返回该票向，否则 null。</summary>
    private static int? UniformVoteAll(IReadOnlyList<PageVote> pages)
    {
        int? mode = null;
        foreach (PageVote p in pages)
        {
            if (p.IsLandscape || p.ClassifierTurns is not { } t || p.ClassifierConfidence < UniformConfFloor) continue;
            if (mode is null) mode = t;
            else if (mode != t) return null;
        }
        return mode;
    }

    /// <summary>参与同向采纳的有票页数（非横版、conf ≥ floor）。</summary>
    private static int UniformVoteCount(IReadOnlyList<PageVote> pages) =>
        pages.Count(p => !p.IsLandscape && p.ClassifierTurns is not null && p.ClassifierConfidence >= UniformConfFloor);
}
