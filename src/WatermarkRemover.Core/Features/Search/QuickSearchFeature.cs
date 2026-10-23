namespace WatermarkRemover.Core.Features.Search;

/// <summary>
/// The 快速搜索 capability (快速搜索页). Like <see cref="Netdisk.NetdiskFeature"/> it is a
/// navigation entry rather than a file processor: the page queries the built-in NTFS index and
/// lets the user open or reveal a hit. Nothing is ever routed through <see cref="Run"/>.
/// </summary>
public sealed class QuickSearchFeature : IFeature
{
    public string Id => "search.page";

    public string DisplayName => "快速搜索";

    public string ProcessingText => "正在搜索…";

    public FeaturePageKind PageKind => FeaturePageKind.Search;

    public string Glyph => "\uE721"; // Search

    public string Tagline => "NTFS 索引 · 文件名秒搜 · 打开/定位";

    /// <summary>No existing file is ever handled by the search page.</summary>
    public bool CanHandle(string filePath) => false;

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("快速搜索不处理已有文件；结果由内置索引直接返回。");
}
