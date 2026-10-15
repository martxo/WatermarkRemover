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
    };
}
