using WatermarkRemover.Core.Features.Naming;

namespace WatermarkRemover.Tests.Core.Naming;

/// <summary>
/// Document type scoring: ClassifyDetailed exposes the winner with raw scores (the report's
/// classify_low_margin rule needs them), while Classify keeps both the absolute-score and the
/// margin gate so weak matches stay unclassified.
/// </summary>
public class DocumentClassifierTests
{
    private static readonly string[] LicenseText =
    {
        "营业执照",
        "统一社会信用代码 91350200MA1234567X",
        "组成形式 个体工商户",
        "注册日期 2021年05月28日",
        "经营范围 道路货物运输",
        "登记机关 玉林市市场监督管理局",
    };

    private static readonly string[] IdCardText =
    {
        "居民身份证",
        "姓名 王小明",
        "出生 1990年3月4日",
        "住址 广西玉林市玉州区",
    };

    private static readonly string[] HouseholdText =
    {
        "居民户口簿",
        "常住人口登记卡",
        "户主 林孝明",
        "住址 广西玉林市茂林镇车垌村大塘100号",
    };

    [Fact]
    public void License_text_classifies_with_a_clear_margin()
    {
        (DocumentProfile? profile, int score, int runnerUp) =
            DocumentClassifier.ClassifyDetailed(string.Join("\n", LicenseText));

        Assert.NotNull(profile);
        Assert.Equal("business-license", profile!.TypeId);
        Assert.True(score >= 6, $"absolute score was {score}");
        Assert.True(score - runnerUp >= 3, $"margin was {score - runnerUp}");
        Assert.Same(profile, DocumentClassifier.Classify(string.Join("\n", LicenseText)));
    }

    [Fact]
    public void Id_card_text_classifies_as_id_card()
    {
        DocumentProfile? profile = DocumentClassifier.Classify(string.Join("\n", IdCardText));

        Assert.Equal("id-card", profile?.TypeId);
    }

    [Fact]
    public void Household_register_text_classifies_as_household_register()
    {
        DocumentProfile? profile = DocumentClassifier.Classify(string.Join("\n", HouseholdText));

        Assert.Equal("household-register", profile?.TypeId);
    }

    [Fact]
    public void Borderline_text_is_visible_in_detailed_but_rejected_by_classify()
    {
        // 两种文档的锚点各得 7 分：ClassifyDetailed 如实暴露平局（报告的低区分度规则要看到），
        // Classify 则因区分度不足保持未分类 —— 不猜。
        const string text = "身份证 住址 出生 营业执照 经营范围";

        (DocumentProfile? profile, int score, int runnerUp) = DocumentClassifier.ClassifyDetailed(text);

        Assert.Equal("business-license", profile?.TypeId);
        Assert.Equal(7, score);
        Assert.Equal(7, runnerUp);
        Assert.Null(DocumentClassifier.Classify(text));
    }

    [Fact]
    public void Empty_and_unrelated_text_stay_unclassified()
    {
        (DocumentProfile? empty, int emptyScore, int emptyRunnerUp) =
            DocumentClassifier.ClassifyDetailed("   ");
        Assert.Null(empty);
        Assert.Equal(0, emptyScore);
        Assert.Equal(0, emptyRunnerUp);
        Assert.Null(DocumentClassifier.Classify(string.Empty));

        const string unrelated = "会议纪要 第三季度项目复盘 参会人员签字";
        (DocumentProfile? profile, int score, _) = DocumentClassifier.ClassifyDetailed(unrelated);
        Assert.Null(profile);
        Assert.Equal(0, score);
        Assert.Null(DocumentClassifier.Classify(unrelated));
    }
}
