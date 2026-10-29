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

    /// <summary>No existing file is ever handled by the search page.</summary>
    public bool CanHandle(string filePath) => false;

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("快速搜索不处理已有文件；结果由内置索引直接返回。");
}
