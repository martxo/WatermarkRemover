namespace WatermarkRemover.Core.Features.Netdisk;

/// <summary>
/// The 网盘 capability (网盘页). It is a navigation entry rather than a file processor: the page is
/// the hub that opens the management window or quick-uploads files, so nothing is ever routed
/// through <see cref="Run"/>.
/// </summary>
public sealed class NetdiskFeature : IFeature
{
    public string Id => "netdisk.page";

    public string DisplayName => "网盘";

    /// <summary>No existing file is ever handled by the netdisk feature.</summary>
    public bool CanHandle(string filePath) => false;

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("网盘不处理已存在的文件；传输由网盘管理窗口直接完成。");
}
