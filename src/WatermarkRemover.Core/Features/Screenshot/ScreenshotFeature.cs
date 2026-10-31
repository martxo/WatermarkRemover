namespace WatermarkRemover.Core.Features.Screenshot;

/// <summary>
/// The 截图 capability (截图页). Like the search / netdisk pages it is a navigation entry rather
/// than a file processor: the overlay produces a PNG on its own, so nothing is ever routed
/// through <see cref="Run"/>.
/// </summary>
public sealed class ScreenshotFeature : IFeature
{
    public string Id => "screenshot.page";

    public string DisplayName => "截图";

    /// <summary>No existing file is processed by the screenshot page.</summary>
    public bool CanHandle(string filePath) => false;

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("截图不处理已有文件；图片由全屏选区直接生成。");
}
