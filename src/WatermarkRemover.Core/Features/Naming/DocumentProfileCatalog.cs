namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// The registry of known document types. To support a new file type, add one
/// <see cref="DocumentProfile"/> here — classification, name extraction and naming pick it up
/// automatically. Anchors are weighted 1-5: distinctive markers heavy, generic words light.
/// </summary>
public static class DocumentProfileCatalog
{
    public static IReadOnlyList<DocumentProfile> All { get; } = new[]
    {
        // 营业执照：主体类型不同，姓名标注词不同（《市场主体登记管理条例实施细则》第二十三条）。
        new DocumentProfile
        {
            TypeId = "business-license",
            TypeShortName = "执照",
            Anchors = new[]
            {
                new ProfileAnchor(@"统一社会信用代码", 5),
                new ProfileAnchor(@"营业执照", 5),
                new ProfileAnchor(@"组成形式", 4),
                new ProfileAnchor(@"注册日期|成立日期", 2),
                new ProfileAnchor(@"经营范围|登记机关", 2),
                new ProfileAnchor(@"法定代表人|执行事务合伙人|投资人|经营者|负责人", 1),
            },
            // Longest label first: when OCR truncates a label the shorter suffix is the fallback.
            NameLabels = new[] { "执行事务合伙人", "法定代表人", "投资人", "经营者", "负责人" },
        },

        // 居民身份证
        new DocumentProfile
        {
            TypeId = "id-card",
            TypeShortName = "身份证",
            Anchors = new[]
            {
                new ProfileAnchor(@"公民身份号码|居民身份证", 5),
                new ProfileAnchor(@"身份证", 3),
                new ProfileAnchor(@"出生|民族|性别", 2),
                new ProfileAnchor(@"住址|签发机关|有效期", 2),
            },
            NameLabels = new[] { "姓名" },
        },

        // 居民户口簿
        new DocumentProfile
        {
            TypeId = "household-register",
            TypeShortName = "户口本",
            Anchors = new[]
            {
                new ProfileAnchor(@"居民户口簿", 5),
                new ProfileAnchor(@"常住人口登记卡", 4),
                new ProfileAnchor(@"户主", 3),
                new ProfileAnchor(@"户口|住址", 2),
            },
            NameLabels = new[] { "户主", "姓名" },
        },

        // 银行流水：账户交易明细单。客户名称可能是个人或企业名（对公账户），均作为命名主体。
        // 企业户名超长且带字母数字——person-name shapes truncate them, so the whole labelled
        // value is taken (CompanyLabel handles both a 3-char person and a 20-char company).
        new DocumentProfile
        {
            TypeId = "bank-statement",
            TypeShortName = "流水",
            Anchors = new[]
            {
                new ProfileAnchor(@"存款账户明细查询|交易明细|账户明细", 5),
                new ProfileAnchor(@"客户名称", 2),
                new ProfileAnchor(@"打印机构|操作柜员", 2),
                new ProfileAnchor(@"卡号/账号|账号/卡号|款项账号", 1),
                new ProfileAnchor(@"币种", 1),
            },
            NameLabels = new[] { "客户名称" },
            NameStrategy = NameExtractionStrategy.CompanyLabel,
        },

        // 个人信用报告（详细版/本人版）：人行征信中心出品，版式固定——「被查询者姓名」表
        // 恒在首页顶部条带，名字就是标签正下方那一格。命名走固定区域几何定位
        // （App 侧 CreditReportNameLocator），文本通道刻意不猜名：水印压格与拼版扫描会把
        // 全页 OCR 行序打碎，从碎行猜名字必然出错（高级领导征信）。
        new DocumentProfile
        {
            TypeId = "credit-report",
            TypeShortName = "征信",
            Anchors = new[]
            {
                new ProfileAnchor(@"个人信用报告", 5),
                new ProfileAnchor(@"被查询者姓名", 5),
                new ProfileAnchor(@"征信中心", 4),
                new ProfileAnchor(@"异议信息提示", 4),
                new ProfileAnchor(@"信息概要", 2),
                new ProfileAnchor(@"查询机构|查询原因", 2),
                new ProfileAnchor(@"报告编号", 1),
            },
            NameLabels = new[] { "被查询者姓名" },
            NameStrategy = NameExtractionStrategy.FixedRegion,
        },

        // 企业信用报告：人行征信中心出品的企业版。个人版锚词「个人信用报告/被查询者姓名」
        // 不出现，靠「企业信用报告/企业名称」区分——否则「征信中心/信息概要/报告编号」这些
        // 两版共有的词会把它误分类成个人版，再被个人版的固定区域定位拖进死路。命名主体是
        // 企业名称（长名，含括号/字母数字），走 CompanyLabel 整值提取。
        new DocumentProfile
        {
            TypeId = "enterprise-credit-report",
            TypeShortName = "企业征信",
            Anchors = new[]
            {
                new ProfileAnchor(@"企业信用报告", 5),
                new ProfileAnchor(@"企业名称", 4),
                new ProfileAnchor(@"机构信用代码", 2),
                new ProfileAnchor(@"信息概要", 2),
                new ProfileAnchor(@"报告编号", 1),
                // 两版共有：轻权重，仅凑分，永远不该单独决定分类。
                new ProfileAnchor(@"征信中心", 1),
            },
            NameLabels = new[] { "企业名称" },
            NameStrategy = NameExtractionStrategy.CompanyLabel,
        },
    };
}
