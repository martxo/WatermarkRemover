using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Geometry;

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

    /// <summary>Real WeChat OCR output of a landscape licence scanned rotated 90°:「经营者」
    /// comes back as two one-char rows with the name as its own row elsewhere, so no label
    /// association survives — only the standalone-row fallback can name this document.</summary>
    private static readonly string[] RotatedLicenseLines =
    {
        "营业执照",
        "统一社会信用代码",
        "维码登录‘",
        "企业信用信息公示",
        "92450903MAK04HH77R",
        "了解更多登记",
        "备案、许可、监管信息。",
        "称 玉林市福绵区三层楼便利店(个体工商户)",
        "组成形式个人经营",
        "型",
        "个体工商户",
        "日 期2025年11月07日",
        "营",
        "者",
        "经营场所玉林市福绵区成均镇百石村分界坡66号",
        "梁桂",
        "活动)",
        "登记机关",
        "月日",
        "2025",
        "名",
        "注册",
        "类经",
        "经营范围",
        "企业信用信息公",
        "国家市场监督管理总局监制",
    };

    /// <summary>Real WeChat OCR output of a bank statement's first page.</summary>
    private static readonly string[] BankStatementLines =
    {
        "存款账户明细查询",
        "打印机构:5058",
        "操作柜员:510951",
        "机构名称:玉林市区农村信用合作联杜玉东信用社",
        "打印日期:20250926",
        "卡号/账号:6231330500012349382",
        "款项账号:505811010110225598",
        "客户名称:廖丹",
        "查询起止日期:20240901 -",
        "币种:人民币",
        "账号/卡号",
        "对方账号",
        "对方户名",
        "摘要码及描述",
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
    public void Rotated_license_with_shredded_label_is_named_via_the_standalone_row()
    {
        var result = SmartNamingService.TryBuildName(RotatedLicenseLines);

        Assert.NotNull(result);
        Assert.Equal("梁桂", result!.PersonName);
        Assert.Equal("执照", result.TypeShortName);
        Assert.Equal("梁桂执照", result.SuggestedStem);
    }

    [Fact]
    public void Bank_statement_is_classified_and_named_from_the_customer_field()
    {
        var result = SmartNamingService.TryBuildName(BankStatementLines);

        Assert.NotNull(result);
        Assert.Equal("廖丹", result!.PersonName);
        Assert.Equal("流水", result.TypeShortName);
        Assert.Equal("廖丹流水", result.SuggestedStem);
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

/// <summary>
/// The personal credit report (detailed / self-query edition, 人民银行征信中心). Its layout is
/// fixed: the「被查询者姓名」header table sits in the top band of page 1 and the subject's name
/// is the cell directly below the label. Naming therefore never guesses from OCR text order
/// (FixedRegion strategy — the App-side CreditReportNameLocator crops that cell); the text
/// pipeline only classifies the type. Shredded full-page OCR once produced 高级领导征信.
/// </summary>
public class CreditReportNamingTests
{
    /// <summary>OCR reading order of the report's first page (header table → value row →
    /// identity section → spouse table).</summary>
    private static readonly string[] CreditReportLines =
    {
        "个人信用报告",
        "(本人版)",
        "报告编号:2026101700001234567890 报告时间:2026.10.19 11:40:53",
        "被查询者姓名 证件类型 证件号码 查询机构 查询原因",
        "陈圣诈 身份证 452501198406021254 本人 本人查询(自查/异议)",
        "异议信息提示",
        "信息主体对信用报告内容提出了0项异议且正在处理中",
        "一个人基本信息",
        "(一)身份信息",
        "性别 出生日期 婚姻状况 就业状况",
        "男 1984.09.02 已婚 已就业",
        "(二)配偶信息",
        "姓名 证件类型 证件号码 工作单位 职务电话",
        "宁开珍 身份证 452501198702145520 玉林市玉州区名山家具 15207795450",
    };

    private static readonly DocumentProfile CreditReport =
        DocumentProfileCatalog.All.First(p => p.TypeId == "credit-report");

    [Fact]
    public void Credit_report_is_classified_but_never_named_from_text_order()
    {
        // Even the clean, correctly ordered fixture must not yield a name here: fixed-region
        // types are named geometrically from the page image, never from OCR text order.
        Assert.Equal("credit-report", DocumentClassifier.Classify(string.Join("\n", CreditReportLines))?.TypeId);
        Assert.Equal(NameExtractionStrategy.FixedRegion, CreditReport.NameStrategy);
        Assert.Null(SmartNamingService.TryBuildName(CreditReportLines));
    }

    [Fact]
    public void A_bank_statement_is_never_misread_as_a_credit_report()
    {
        var bankLines = new[]
        {
            "存款账户明细查询", "客户名称:廖丹", "卡号/账号:6231330500012349382", "币种:人民币",
        };

        Assert.Equal("bank-statement", DocumentClassifier.Classify(string.Join("\n", bankLines))?.TypeId);
    }

    [Fact]
    public void Bank_statement_with_a_corporate_customer_is_named_from_the_full_value()
    {
        var bankLines = new[]
        {
            "存款账户明细查询",
            "客户名称 广西玉林三和商贸有限公司",
            "卡号/账号 6231330500012349382",
            "币种 人民币",
            "交易日期 交易金额 余额",
        };

        var result = SmartNamingService.TryBuildName(bankLines);
        Assert.NotNull(result);
        Assert.Equal("广西玉林三和商贸有限公司流水", result.SuggestedStem);
    }

    /// <summary>Real OCR line order of the 2026-10-19 field scan (landscape, watermark-pressed):
    /// WeChat OCR shredded the header row into per-cell lines and the value cells came back as
    /// garbled address fragments — the subject's name is simply absent from the OCR output.
    /// Text-order guessing on such input produced 高级领导征信; the FixedRegion strategy makes
    /// the text pipeline refuse to name it (the region locator owns the name instead).</summary>
    private static readonly string[] ShreddedHeaderRealScanLines =
    {
        "个人信用报告",
        "(本人版)",
        "被查询者姓名",
        "被查询者证件类型",
        "被查询者证件号码",
        "查询机构",
        "查询原因",
        "西杜族台治区玉林市广咀牌台县",
        "顿谷镇顿谷村门楼坡队",
        "本人查询(自助查询机)",
    };

    [Fact]
    public void A_shredded_per_cell_header_never_yields_a_label_fragment_as_name()
    {
        // Regression for v1.14.0 naming every credit report 被查询者征信 / 高级领导征信: the
        // text channel must return null (timestamp name kept); the region locator finds the name.
        Assert.Null(SmartNamingService.TryBuildName(ShreddedHeaderRealScanLines));
    }
}

/// <summary>
/// 竖排标签条 regression (v1.15.0 名类执照 / 名光执照): a licence's spread label column
/// (名 称 / 类 型 / 经 营 者) comes back from the OCR engine as one tall box merging the rows'
/// first characters — 名类 / 类经 — text shaped exactly like a 2-char person name. Whichever
/// OCR line order puts such a strip before the real name row, the extractor's first-wins rule
/// names the file after it. Bounded input lets the naming pipeline drop these strips on
/// geometry alone (height ≫ width).
/// </summary>
public class VerticalLabelStripTests
{
    private static RecognizedLine Line(string text, double x, double y, double width, double height) =>
        new(text, new RectD(x, y, width, height));

    /// <summary>2026-10-20 08:12 real scan (upright licence): strip 名类 (名称+类型 first
    /// chars, box 0.01x0.07) emitted before the name merges — 名类执照 was the field result.</summary>
    private static readonly RecognizedLine[] UprightLicenseWithStrip =
    {
        Line("营业执照", 0.33, 0.31, 0.37, 0.12),
        Line("统一社会信用代码", 0.11, 0.34, 0.14, 0.03),
        Line("名类", 0.11, 0.50, 0.01, 0.07),
        Line("称玉林市福绵区三层楼便利店(个体工商户)", 0.19, 0.50, 0.29, 0.03),
        Line("型个体工商户", 0.19, 0.54, 0.10, 0.03),
        Line("者梁桂", 0.19, 0.58, 0.06, 0.03),
        Line("登记机关", 0.67, 0.82, 0.11, 0.03),
    };

    /// <summary>Shredded-label variant: label column fully shredded, so only the standalone-row
    /// fallback can name the page — where the strip would win purely by line order.</summary>
    private static readonly RecognizedLine[] ShreddedLicenseWithStrip =
    {
        Line("营业执照", 0.33, 0.31, 0.37, 0.12),
        Line("统一社会信用代码", 0.11, 0.34, 0.14, 0.03),
        Line("名类", 0.11, 0.50, 0.01, 0.07),
        Line("梁桂", 0.19, 0.58, 0.06, 0.03),
        Line("登记机关", 0.67, 0.82, 0.11, 0.03),
    };

    [Fact]
    public void Vertical_strip_is_dropped_and_the_label_merge_still_names_the_licence()
    {
        var result = SmartNamingService.TryBuildName(UprightLicenseWithStrip);

        Assert.NotNull(result);
        Assert.Equal("梁桂", result!.PersonName);
        Assert.Equal("梁桂执照", result.SuggestedStem);
    }

    [Fact]
    public void Vertical_strip_never_wins_the_standalone_fallback_by_line_order()
    {
        // The string pipeline on these texts names the strip (名类执照 — the field bug);
        // bounded input must not.
        Assert.Equal("名类执照", SmartNamingService.TryBuildName(
            ShreddedLicenseWithStrip.Select(l => l.Text).ToList())!.SuggestedStem);

        var result = SmartNamingService.TryBuildName(ShreddedLicenseWithStrip);
        Assert.NotNull(result);
        Assert.Equal("梁桂", result!.PersonName);
    }

    [Fact]
    public void Horizontal_name_rows_are_never_dropped_by_the_strip_filter()
    {
        var lines = new[]
        {
            Line("营业执照", 0.33, 0.31, 0.37, 0.12),
            Line("统一社会信用代码", 0.11, 0.34, 0.14, 0.03),
            Line("梁桂", 0.19, 0.58, 0.06, 0.03),
        };

        Assert.Equal("梁桂执照", SmartNamingService.TryBuildName(lines)!.SuggestedStem);
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
    public void The_first_confident_candidate_wins_over_later_tail_noise()
    {
        // The 法定代表人 label yields a proper name; the later 1-char tail match ("者" +
        // 子分信息) must not override it.
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

    [Fact]
    public void Standalone_fallback_keeps_out_titles_and_page_furniture()
    {
        // Every row here is 2-4 CJK chars, but none is a name: 营业执照 is a profile anchor,
        // 登记机关 carries field words, 月日 is calendar page furniture — none of them may
        // pass the standalone-row fallback.
        var lines = new[] { "营业执照", "登记机关", "月日", "组成形式个人经营" };

        Assert.Null(NameExtractor.Extract(lines, License));
    }
}

/// <summary>
/// The enterprise credit report (人民征信中心, corporate edition), fixtures in REAL OCR line
/// order from two 2026-10 scans. Its「征信中心/信息概要」anchors also live on the PERSONAL report —
/// before it had its own profile it classified as credit-report and was dragged into a dead-end
/// fixed-region lookup. The subject is a PERSON (files are filed per customer), but the name sits
/// in table cells that may not even be on the scanned pages (万花筒 scan never surfaced one) and
/// the label/value pairs come out of OCR in scrambled order (宏亚's value lands three lines below
/// its label) — extraction is a guaranteed miss, so the type names to the「谁的企业征信」
/// placeholder for the user to rename by hand.
/// </summary>
public class EnterpriseCreditReportNamingTests
{
    /// <summary>万花筒广告策划 scan — 2 pages, no person name surfaced at all.</summary>
    private static readonly string[] WanhautongLines =
    {
        "征信中心",
        "N0.2026102111382680088748",
        "报告说明",
        "企业信用报告",
        "(自主查询版)",
        "公共信息",
        "企业名称:",
        "玉林市万花筒广告策划有限公司",
        "中征码:",
        "4509020002087410",
        "统一社会信用代码:91450900569070281B",
        "查询机构:",
        "中国人民银行玉林市分行",
        "报告时间:",
        "2026-10-21T11:38:26",
        "第1页/共4页",
        "身份标识",
        "注册资本及主要出资人信息",
        "信息概要",
        "法定代表人/非法人组织负责",
    };

    /// <summary>宏亚电子厂 scan — the company value lands 3 lines BELOW its label.</summary>
    private static readonly string[] HongyaLines =
    {
        "征信中心",
        "N0.2026101915594786089488",
        "企业信用报告",
        "(自主查询版)",
        "企业名称:",
        "中征码:",
        "统一社会信用代码:92450921MADMU4QN9E",
        "容县宏亚电子厂(个体工商户)",
        "查询机构;",
        "中国人民银行玉林市分行",
        "身份标识",
        "企业名称",
        "容县宏亚电子厂(个体工商户)",
        "实际控制人",
        "农亚红",
        "信息概要",
    };

    [Fact]
    public void Enterprise_report_is_classified_as_its_own_type()
    {
        Assert.Equal(
            "enterprise-credit-report",
            DocumentClassifier.Classify(string.Join("\n", WanhautongLines))?.TypeId);
        Assert.Equal(
            "enterprise-credit-report",
            DocumentClassifier.Classify(string.Join("\n", HongyaLines))?.TypeId);
    }

    [Fact]
    public void Enterprise_report_names_to_the_placeholder_even_when_a_person_name_appears()
    {
        // Hongya's OCR DOES contain 农亚红 — the placeholder stands anyway: a name floating
        // in a table without its label is not provably the filing person, and a wrong name is
        // worse than an obvious「谁」the user renames by hand.
        var wanhua = SmartNamingService.TryBuildName(WanhautongLines);
        var hongya = SmartNamingService.TryBuildName(HongyaLines);

        Assert.Equal("谁的企业征信", wanhua?.SuggestedStem);
        Assert.Equal("谁的企业征信", hongya?.SuggestedStem);
    }
}
