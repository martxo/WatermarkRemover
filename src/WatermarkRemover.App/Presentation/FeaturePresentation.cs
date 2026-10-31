using WatermarkRemover.Core.Features;

namespace WatermarkRemover.App.Presentation;

/// <summary>Which shell page renders a feature. Purely presentational — nothing below the UI
/// layer knows about it, so a headless host can run the same features without a page concept.</summary>
public enum FeaturePageKind
{
    /// <summary>Drop zone + job list (file processors: watermark, OCR).</summary>
    Files,

    /// <summary>Scanner strip + job list.</summary>
    Scan,

    /// <summary>Full-height search page (no job list).</summary>
    Search,

    /// <summary>Netdisk entry hub + job list.</summary>
    Netdisk,

    /// <summary>Phone receiver panel + job list.</summary>
    Transfer,

    /// <summary>Screenshot launcher + job list.</summary>
    Screenshot,
}

/// <summary>How one feature looks in the shell: icon, one-line caption, page kind, busy text.</summary>
public sealed record FeaturePresentation(
    string Glyph,
    string Tagline,
    FeaturePageKind PageKind,
    string? ProcessingText)
{
    /// <summary>The generic look every unregistered feature gets.</summary>
    public static FeaturePresentation Default(IFeature feature) => new(
        "\uE7C3", // Document
        string.Empty,
        FeaturePageKind.Files,
        $"正在处理「{feature.DisplayName}」…");
}

/// <summary>
/// Presentation metadata for features, kept out of <see cref="IFeature"/>: icons, captions and
/// shell page kinds are UI facts, and a Core feature must not carry them (that was the layering
/// leak). Keyed by the stable feature id; unknown ids fall back to
/// <see cref="FeaturePresentation.Default"/> so a new feature renders without touching the shell.
/// </summary>
public static class FeaturePresentationCatalog
{
    private static readonly Dictionary<string, FeaturePresentation> Map = new(StringComparer.Ordinal)
    {
        ["scan.page"] = new("\uE7F4", "自动命名 · 自动调正 · 自动删白", FeaturePageKind.Scan, "正在扫描…"),
        ["search.page"] = new("\uE721", "NTFS 索引 · 文件名秒搜 · 打开/定位", FeaturePageKind.Search, "正在搜索…"),
        ["watermark.badge"] = new("\uE75C", "拖入 PDF → 去水印 · 智能命名 · 页面管理", FeaturePageKind.Files,
            "正在分析并去除水印…"),
        ["ocr.text"] = new("\uE7C3", "拖入 PDF 或图片 → 识别文字 → 生成同名 txt", FeaturePageKind.Files,
            "正在渲染页面并识别文字…"),
        ["screenshot.page"] = new("\uE722", "区域截图 · 标注 · 自动复制并保存 PNG", FeaturePageKind.Screenshot,
            "正在截图…"),
        ["netdisk.page"] = new("\uE753", "网盘账号管理 · 上传下载 · 快捷传", FeaturePageKind.Netdisk, "正在传输…"),
        ["transfer.page"] = new("\uE8EA", "手机扫码 → 浏览器选文件 → 传到电脑", FeaturePageKind.Transfer,
            "正在接收…"),
    };

    public static FeaturePresentation For(IFeature feature) =>
        Map.TryGetValue(feature.Id, out FeaturePresentation? presentation)
            ? presentation
            : FeaturePresentation.Default(feature);

    /// <summary>Busy text for a job row; never null.</summary>
    public static string ProcessingText(IFeature feature) =>
        For(feature).ProcessingText ?? $"正在处理「{feature.DisplayName}」…";
}
