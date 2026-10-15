using WatermarkRemover.Core.Features.Naming;

namespace WatermarkRemover.Tests.Core.Naming;

/// <summary>
/// Classification + name extraction over OCR text. The business-license fixture is the real
/// recogniser output of 扫描全能王.pdf (individual business licence, truncated 经营者 label).
/// </summary>
public class SmartNamingServiceTests
{
    /// <summary>Real WeChat OCR output of the business-licence sample (labels are truncated).</summary>
    private static readonly string[] BusinessLicenseLines =
    {
        "营业执照",
        "统一社会信用代码",
        "家企业信用信息公示",
        "92450900MA5QH0W491",
        "系统’了解更多登记",
        "组成形式个人经营",
        "型个体工商户",
        "注册日期2021年05月28日",
        "经 营场 所广西玉林市茂林镇车垌村大塘100号",
        "者林孝明",
        "经营范围许可项目:道路货物运输(不含危险货物)",
        "登记机关",
        "国家企业信用信息公示系统网址:",
    };

    private static readonly string[] IdCardLines =
    {
        "居民身份证",
        "姓名 王小明",
        "性别 男 民族 汉",
        "出生 1990年3月4日",
        "住址 广西玉林市玉州区",
        "公民身份号码 450902199003040018",
    };

    private static readonly string[] HouseholdLines =
    {
        "居民户口簿",
        "户主",
        "林孝明",
        "常住人口登记卡",
        "住址 广西玉林市茂林镇车垌村大塘100号",
    };

    [Fact]
    public void Business_license_is_classified_and_name_extracted_despite_truncated_label()
    {
        var result = SmartNamingService.TryBuildName(BusinessLicenseLines);

        Assert.NotNull(result);
        Assert.Equal("林孝明", result!.PersonName);
        Assert.Equal("执照", result.TypeShortName);
        Assert.Equal("林孝明执照", result.SuggestedStem);
    }

    [Fact]
    public void Id_card_is_classified_and_name_extracted()
    {
        var result = SmartNamingService.TryBuildName(IdCardLines);

        Assert.NotNull(result);
        Assert.Equal("王小明", result!.PersonName);
        Assert.Equal("身份证", result.TypeShortName);
    }

    [Fact]
    public void Household_register_is_classified_and_name_extracted()
    {
        var result = SmartNamingService.TryBuildName(HouseholdLines);

        Assert.NotNull(result);
        Assert.Equal("林孝明", result!.PersonName);
        Assert.Equal("户口本", result.TypeShortName);
    }

    [Fact]
    public void Household_register_is_never_misread_as_a_business_license()
    {
        var profile = WatermarkRemover.Core.Features.Naming.DocumentClassifier
            .Classify(string.Join("\n", HouseholdLines));

        Assert.Equal("household-register", profile?.TypeId);
    }

    [Fact]
    public void Unknown_text_stays_unclassified()
    {
        var result = SmartNamingService.TryBuildName(new[] { "随便一段没有特征的文字", "这里也是" });

        Assert.Null(result);
    }

    [Fact]
    public void Empty_input_yields_null()
    {
        Assert.Null(SmartNamingService.TryBuildName(Array.Empty<string>()));
        Assert.Null(SmartNamingService.TryBuildName(new[] { "   " }));
    }

    [Fact]
    public void Type_recognised_but_no_name_found_yields_null()
    {
        // Licence markers present but nobody's name anywhere.
        var lines = new[]
        {
            "营业执照", "统一社会信用代码", "组成形式个人经营",
            "注册日期2021年05月28日", "经营范围道路货物运输", "登记机关",
        };

        Assert.Null(SmartNamingService.TryBuildName(lines));
    }
}

/// <summary>Name extraction shapes: same line, next line, truncated labels, noise rejection.</summary>
public class NameExtractorTests
{
    private static readonly DocumentProfile License = DocumentProfileCatalog.All[0];

    [Fact]
    public void Full_label_same_line_yields_the_name()
    {
        var lines = new[] { "经营者：张三丰", "其他内容" };

        Assert.Equal("张三丰", NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void Truncated_label_tail_still_yields_the_name()
    {
        // The real sample: 经营者 lost its first two characters to OCR.
        var lines = new[] { "者林孝明", "经营范围道路运输" };

        Assert.Equal("林孝明", NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void Label_on_its_own_line_takes_the_name_from_the_next_line()
    {
        var lines = new[] { "法定代表人", "李四海", "注册资本 100 万" };

        Assert.Equal("李四海", NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void Two_character_names_are_accepted()
    {
        var lines = new[] { "经营者张伟" };

        Assert.Equal("张伟", NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void Four_character_names_are_accepted()
    {
        var lines = new[] { "经营者欧阳晴岚" };

        Assert.Equal("欧阳晴岚", NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void Noise_after_the_label_is_rejected()
    {
        // 18-digit number and ASCII garbage right where a name would be.
        var lines = new[] { "经营者4509021990030400", "abc def" };

        Assert.Null(NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void The_best_candidate_wins_over_weaker_ones()
    {
        // "者" tail (1-char label match) sees 子分信息 which starts with a common surname-ish char,
        // but the full 法定代表人 label with a proper 3-char name must score higher.
        var lines = new[]
        {
            "者子分信息",
            "法定代表人：赵大河",
        };

        Assert.Equal("赵大河", NameExtractor.Extract(lines, License));
    }

    [Fact]
    public void Single_char_generic_tail_names_are_skipped()
    {
        // "人" + a name must not be harvested by tail matching (人 is too generic to anchor on);
        // without the skip this line would yield 李家明.
        var lines = new[] { "共 4 人李家明参加" };

        Assert.Null(NameExtractor.Extract(lines, License));
    }
}
