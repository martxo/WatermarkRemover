using System.Text.Json.Serialization;

namespace WatermarkRemover.Scan.Core.Models;

/// <summary>
/// The full scan configuration. Every property corresponds to a user-facing choice in the target's
/// "详细设置" dialog, so parity can be checked item by item (see docs/功能对照表.md).
/// </summary>
public sealed class ScanSettings
{
    // --- 基本设置 ---------------------------------------------------------
    public ColorMode Color { get; set; } = ColorMode.Color24;

    public ScanResolution Resolution { get; set; } = ScanResolution.Dpi300;

    public PaperSize Paper { get; set; } = PaperSize.Auto;

    /// <summary>Duplex handling. Ignored when <see cref="Feed"/> is <see cref="FeedMode.Flatbed"/>.</summary>
    public DuplexMode Duplex { get; set; } = DuplexMode.Off;

    /// <summary>How the duplex batch delivers images (see <see cref="DuplexFeedOrder"/>).
    /// Defaults to interleaved: every current capture channel (Brother ADK duplex, WIA
    /// HandlingDuplex) transfers both sides per sheet in one pass.</summary>
    public DuplexFeedOrder DuplexFeedOrder { get; set; } = DuplexFeedOrder.Interleaved;

    public FeedMode Feed { get; set; } = FeedMode.Auto;

    // --- 图像处理 ---------------------------------------------------------
    /// <summary>0 disables moire suppression; 1..5 raise the strength (target: SCN_MOIRE_REMOVAL_LEVEL).</summary>
    public int MoireRemovalLevel { get; set; }

    /// <summary>
    /// 黑白二值化临界值（引擎 monoThreshold，0-255）；-1 = 引擎自动。仅 <see cref="ColorMode.BlackWhite"/> 时生效
    /// （官方语义：SCN_BW_THRESHOLD）。
    /// </summary>
    public int MonoThreshold { get; set; } = -1;

    /// <summary>
    /// 误差扩散亮度（引擎 diffusionTone.brightness）；仅 <see cref="ColorMode.GrayDiffusion"/> 时生效。
    /// </summary>
    public int DiffusionBrightness { get; set; }

    /// <summary>
    /// 误差扩散对比度（引擎 diffusionTone.contrast）；仅 <see cref="ColorMode.GrayDiffusion"/> 时生效。
    /// </summary>
    public int DiffusionContrast { get; set; }

    // --- 黑白图像质量（SCN_BW_THRESHOLD / SCN_BLACK_WHITE_INVERSION / SCN_HIGHLIGHT /
    //     SCN_SHADOW / SCN_TEXT_ENHANCEMENT，应用侧由 BwAdjustmentProcessor 消费） ---
    /// <summary>黑白反转（SCN_BLACK_WHITE_INVERSION）。</summary>
    public bool BwInvert { get; set; }

    /// <summary>加亮：细笔画/去噪点（SCN_HIGHLIGHT）。</summary>
    public bool BwHighlight { get; set; }

    /// <summary>阴影：加粗笔画（SCN_SHADOW）。</summary>
    public bool BwShadow { get; set; }

    /// <summary>粗体格式级别（SCN_TEXT_ENHANCEMENT）：笔画膨胀像素数，0 = 关。</summary>
    public int BwBold { get; set; }

    public BlankRemovalLevel BlankRemoval { get; set; } = BlankRemovalLevel.Off;

    public bool RemoveBackground { get; set; }

    public bool CorrectSkew { get; set; } = true;

    /// <summary>
    /// Opt-in per-page content cropping. Default off: the target has no application-side content crop
    /// (its auto size detection is device-side), the WIA driver already delivers paper-extent images,
    /// and cropping each duplex side to its own content makes the two sides of one sheet differ in size.
    /// </summary>
    public bool AutoCrop { get; set; }

    /// <summary>Rotation applied before other processors: 0, 90, 180 or 270 degrees.</summary>
    public int RotationDegrees { get; set; }

    // --- 特殊进纸 ---------------------------------------------------------
    /// <summary>Carrier sheet (纸垫) mode for fragile originals (SCN_CARRIER_SHEET_MODE).</summary>
    public bool CarrierSheetMode { get; set; }

    /// <summary>Plastic card mode (SCN_CARDSLOTPLASTICCARD_SETTING_WINDOWTITLE).</summary>
    public bool PlasticCardMode { get; set; }

    // --- 智能 -------------------------------------------------------------
    /// <summary>Auto colour detection per page; overrides <see cref="Color"/> when enabled.</summary>
    public bool AutoColorMode { get; set; }

    public bool AutoRotate { get; set; }

    // --- 文档 -------------------------------------------------------------
    /// <summary>OCR/document language, e.g. "ja", "en", "zh-Hans". Empty means "auto".</summary>
    public string DocumentLanguage { get; set; } = string.Empty;

    /// <summary>Continuous scan while pages remain in the ADF (target: SCN continuous scan).</summary>
    public bool ContinuousScan { get; set; } = true;

    /// <summary>Device id this settings block was last used with; informational only.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Declared pixel dimensions for <see cref="PaperSize.Auto"/>; used by simulated sources.</summary>
    public int? CustomWidthPx { get; set; }

    public int? CustomHeightPx { get; set; }

    [JsonIgnore]
    public bool IsFlatbed => Feed == FeedMode.Flatbed;

    public ScanSettings Clone() => (ScanSettings)MemberwiseClone();
}
