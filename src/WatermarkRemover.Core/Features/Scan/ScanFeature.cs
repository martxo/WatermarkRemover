namespace WatermarkRemover.Core.Features.Scan;

/// <summary>
/// The scan capability (扫描页). It is a navigation entry and job-card producer rather than a file
/// processor: the scan flow captures pages from a device and writes its PDF output directly, so
/// files are never routed through <see cref="Run"/>. It does accept an existing PDF dropped on its
/// page — the shell turns that into a finished record (rotate / delete / reorder in the strip),
/// which is why <see cref="CanHandle"/> is true for PDFs even though <see cref="Run"/> is not used.
/// </summary>
public sealed class ScanFeature : IFeature
{
    public string Id => "scan.page";

    public string DisplayName => "扫描";

    /// <summary>An existing PDF can be dropped onto the scan page for editing; it never reaches
    /// <see cref="Run"/> — the shell attaches it as a finished record instead.</summary>
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> InputExtensions => new[] { ".pdf" };

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("扫描不处理已存在的文件；扫描输出由 ScanService 直接产出。");
}
