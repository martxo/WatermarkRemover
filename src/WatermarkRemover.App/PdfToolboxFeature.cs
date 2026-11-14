using System.IO;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App;

/// <summary>
/// 统一的 PDF 工作台入口（「PDF 工具」页）：去水印与文字识别不再是两个菜单，而是同一份
/// 记录上的两个能力。按扩展名路由 —— PDF 走去水印管线（含智能命名与页面管理），图片走
/// 文字识别；完成后记录行上也可以就地再发起「识别文字」。
///
/// 刻意沿用 <see cref="WatermarkFeature"/> 的 section id（watermark.badge）：任务日志、
/// 待处理登记和历史记录的归属无需任何迁移。
/// </summary>
public sealed class PdfToolboxFeature : IFeature
{
    private readonly WatermarkFeature _watermark;
    private readonly OcrFeature _ocr;

    public PdfToolboxFeature(WatermarkFeature watermark, OcrFeature ocr)
    {
        _watermark = watermark ?? throw new ArgumentNullException(nameof(watermark));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
    }

    /// <summary>历史 section id —— 保持与旧记录、任务日志连续（见类注释）。</summary>
    public string Id => _watermark.Id;

    public string DisplayName => "PDF 工具";

    public bool CanHandle(string filePath) =>
        _watermark.CanHandle(filePath) || _ocr.CanHandle(filePath);

    public IReadOnlyList<string> InputExtensions =>
        _watermark.InputExtensions
            .Concat(_ocr.InputExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        RouteFor(inputPath).Run(inputPath, context);

    /// <summary>该文件在工作台里对应的处理器：PDF → 去水印，图片 → 文字识别。</summary>
    public IFeature RouteFor(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        bool isImage = !string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        return isImage ? _ocr : _watermark;
    }

    /// <summary>
    /// 记录行上的「识别文字」按钮专用：无论 PDF 还是图片，始终走 OCR 管线。RouteFor 在这里
    /// 不适用 —— 它按扩展名分流，PDF 会被送去水印检测，识别按钮就会 5ms 空转。
    /// </summary>
    public IFeature Ocr => _ocr;
}
