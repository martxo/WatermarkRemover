using WatermarkRemover.Core.Features.Layout;

namespace WatermarkRemover.App.Layout;

/// <summary>
/// The app's layout stage chain — the first pipeline stage (how the page is split), probed once
/// per process. Registration order is priority order and the always-available full-image
/// passthrough is pinned at the top, so a machine missing every model recognises exactly like
/// the direct path. Adding a block = append one factory line here (name + description + probe);
/// settings UI, selection and fallback all follow automatically — same contract as
/// <c>AppOcrEngineChain</c>.
/// </summary>
internal static class AppLayoutStageChain
{
    public static LayoutStageChain Instance { get; } = new(new ILayoutStageFactory[]
    {
        new DelegateLayoutStageFactory(
            "整图直通",
            () => (true, FullImageLayoutStage.Instance, string.Empty),
            "不切分版面，整页交给所选引擎识别（默认，与旧版行为完全一致）。"),
        new DelegateLayoutStageFactory(
            "DocLayout-YOLO 通用版面",
            () =>
            {
                bool ok = DocLayoutYoloStage.TryCreate(out ILayoutStage? stage, out string reason);
                return (ok, stage, reason);
            },
            "通用文档版面模型（约 75MB）：识别标题、段落、表格等版面区域后分区识别，多版式混排文档效果最佳。"),
        new DelegateLayoutStageFactory(
            "PP-DocLayout-S 轻量版面",
            () =>
            {
                bool ok = PpDocLayoutSStage.TryCreate(out ILayoutStage? stage, out string reason);
                return (ok, stage, reason);
            },
            "轻量版面模型（约 4.7MB）：23 类版面区域，包体小、速度快，适合结构简单的文档。"),
        new DelegateLayoutStageFactory(
            "营业执照模板定位",
            () => (true, LicenseTemplateStage.Instance, string.Empty),
            "零模型：按标签锚点（名称 / 法定代表人 / 统一社会信用代码…）定位字段行再识别，仅适用于固定版式证件。"),
    });
}
