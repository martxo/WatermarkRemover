namespace WatermarkRemover.Core.Features.Scan;

/// <summary>
/// The scan capability (扫描页). It is a navigation entry and job-card producer rather than a file
/// processor: the scan flow captures pages from a device and writes its PDF output directly, so
/// files are never routed through <see cref="Run"/>.
/// </summary>
public sealed class ScanFeature : IFeature
{
    public string Id => "scan.page";

    public string DisplayName => "扫描";

    /// <summary>No existing file is ever handled by the scan feature.</summary>
    public bool CanHandle(string filePath) => false;

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("扫描不处理已存在的文件；扫描输出由 ScanService 直接产出。");
}
