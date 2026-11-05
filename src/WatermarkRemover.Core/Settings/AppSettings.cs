using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Core.Settings;

/// <summary>Where the cleaned file is written.</summary>
public enum OutputMode
{
    /// <summary>Write next to the input file.</summary>
    SameDirectory = 0,

    /// <summary>Write into <see cref="AppSettings.OutputDirectory"/>.</summary>
    CustomDirectory = 1,
}

/// <summary>
/// Format version of the settings document. Every saved file carries it, so a later release can
/// migrate an older file instead of guessing what defaults used to mean. Bump only together with
/// a migration arm in SettingsService.
/// </summary>
public static class SettingsSchema
{
    /// <summary>1 = the unversioned files shipped before this field existed.</summary>
    public const int CurrentVersion = 2;
}

/// <summary>User preferences, persisted as JSON alongside the application.</summary>
public sealed class AppSettings
{
    /// <summary>Written by every save; lets an upgrade tell an old layout from a new one.</summary>
    public int SchemaVersion { get; set; } = SettingsSchema.CurrentVersion;

    /// <summary>Suffix appended to the input file name to form the output name.</summary>
    public string OutputSuffix { get; set; } = "_no-watermark";

    public OutputMode OutputMode { get; set; } = OutputMode.SameDirectory;

    /// <summary>Used when <see cref="OutputMode"/> is <see cref="OutputMode.CustomDirectory"/>.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Remove link annotations that belong to the detected watermark.</summary>
    public bool RemoveLinkedAnnotations { get; set; } = true;

    /// <summary>How many files are processed at the same time.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Check the release channel for a newer version when the app starts.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// Quick suffix chips shown next to each processed file. Clicking one appends the suffix to
    /// the file name (clicking again removes it). Managed in the settings window.
    /// </summary>
    public List<string> QuickSuffixes { get; set; } = new() { "征信", "执照", "身份证", "户口本", "房产证" };

    /// <summary>
    /// Name watermark-removal outputs after the document's content (e.g. 林孝明执照.pdf) when
    /// the document type and customer name can be recognised. Falls back to
    /// <see cref="OutputSuffix"/> naming otherwise.
    /// </summary>
    public bool AutoNameByContent { get; set; } = true;

    /// <summary>
    /// Probe only the first page for content-based naming on multi-page documents. Off by
    /// default since 1.16.81 ("全部识别"): naming quality beats the seconds saved, and the
    /// probe is capped at four pages so a long document still cannot stall the pipeline.
    /// </summary>
    public bool NameFromFirstPageOnly { get; set; }

    /// <summary>
    /// Name of the OCR engine the user pinned in the settings OCR section (a factory name from
    /// the engine chain). Empty = pick the best available engine automatically. A preference
    /// whose engine is unavailable falls back to the automatic chain at runtime.
    /// Default since 1.16.81: the WeChat engine (best Chinese document quality); machines
    /// without WeChat fall back to the bundled PP-OCR automatically, so the default is safe.
    /// </summary>
    public string PreferredOcrEngine { get; set; } = WeChatOcrEngineName;

    /// <summary>Factory name of the WeChat engine in the app's OCR engine chain.</summary>
    public const string WeChatOcrEngineName = "微信 OCR";

    /// <summary>
    /// 已经提示过「新默认设置」的版本号（1.16.81 = 11681）。0 = 从未提示。用于那次性提示：
    /// 改动只改代码默认值，绝不在用户不知情时覆盖已保存的选择。
    /// </summary>
    public int DefaultsNoticeVersion { get; set; }

    /// <summary>
    /// Name of the layout block (first pipeline stage) the user pinned in the settings OCR
    /// section (a factory name from the layout stage chain). Empty = full-image passthrough,
    /// i.e. the pre-pipeline behaviour. A preference whose block is unavailable falls back to
    /// the automatic chain at runtime.
    /// </summary>
    public string PreferredLayoutStage { get; set; } = string.Empty;

    /// <summary>Scan page layout (扫描页): 0 = 单面, 1 = 双面长边, 2 = 双面短边.</summary>
    public int ScanDuplex { get; set; }

    /// <summary>盲扫逐页方向裁决 (v1.16.83): 每页独立判定 0/90/180/270 并写各自的 /Rotate，
    /// 任意朝向放纸都能出正向 PDF。模型缺失或引擎不可用时自动回退整份同转（第 1 页裁决），
    /// 再回退预览侧摆正——永不因方向裁决让扫描失败。关闭则固定走整份同转。</summary>
    public bool PerPageOrientationEnabled { get; set; } = true;

    /// <summary>连续扫描: re-arm the scanner as soon as one document lands. Batch work is dozens
    /// of documents a day, so the mode is remembered rather than re-checked every launch.</summary>
    public bool ContinuousScan { get; set; }

    /// <summary>扫描训练数据导出（默认关闭）：每份扫描落一个本地目录，保存生产同域的 256px
    /// 探针、原始帧灰度图、逐页票面与最终 /Rotate 真值，供后续方向模型自监督微调。只写本地、
    /// 不上传；环境变量 WMR_DATASET_EXPORT=1 也可开启（便于不改 UI 直接抓数据）。</summary>
    public bool ScanDatasetExportEnabled { get; set; }

    /// <summary>Scan colour mode (扫描色彩): 0 = 自动, 1 = 彩色, 2 = 灰度, 3 = 黑白. Defaults to
    /// 黑白 — text documents scan ~2x faster and produce far smaller, crisper PDFs.</summary>
    public int ScanColor { get; set; } = 3;

    /// <summary>Where scanned PDFs are written (扫描设置 → 输出目录). Empty = the Pictures
    /// folder, which stays the default so an upgrade never moves existing scans elsewhere.
    /// Independent of the watermark feature's OutputDirectory — scans have no source file.</summary>
    public string ScanOutputDirectory { get; set; } = string.Empty;

    /// <summary>Where phone uploads and share-link PDFs land (手机传文件保存位置). Empty = the
    /// Desktop itself, the pre-setting default — no 「手机传文件」 subfolder is created.</summary>
    public string TransferOutputDirectory { get; set; } = string.Empty;

    /// <summary>Global shortcut that opens the screenshot overlay (截图设置). Stored as the
    /// canonical text form, e.g. <c>Ctrl+Alt+A</c>; a malformed value falls back to the default.</summary>
    public string ScreenshotHotkey { get; set; } =
        WatermarkRemover.Core.Features.Screenshot.ScreenshotHotkey.Default.ToString();

    /// <summary>Global shortcut for a full-screen capture, skipping the region overlay.</summary>
    public string ScreenshotFullScreenHotkey { get; set; } =
        WatermarkRemover.Core.Features.Screenshot.ScreenshotHotkey.FullScreenDefault.ToString();

    /// <summary>Global shortcut that slides the recent-screenshot shelf down.</summary>
    public string ScreenshotShelfHotkey { get; set; } =
        WatermarkRemover.Core.Features.Screenshot.ScreenshotHotkey.ShelfDefault.ToString();

    /// <summary>Where screenshots are saved (截图设置). Empty = 图片\Screenshots.</summary>
    public string ScreenshotOutputDirectory { get; set; } = string.Empty;

    /// <summary>Copy every finished screenshot to the clipboard (截图设置).</summary>
    public bool ScreenshotAutoCopy { get; set; } = true;

    /// <summary>Save every finished screenshot as a PNG file (截图设置).</summary>
    public bool ScreenshotAutoSave { get; set; } = true;

    /// <summary>
    /// 截图 / 标注导出的图片格式：png（默认，无损、兼容性最好）/ webp / jpeg。
    /// WebP 走 SkiaSharp 内置的 libwebp（与 Squoosh 同源的编码器），实测同画质下
    /// 体积约为 PNG 的 1/4~1/2；PDF 内部渲染缓存与 PDF 内嵌图不走这项（保持 PNG/JPEG，
    /// 保证 PDFium 与阅读器可解码）。
    /// </summary>
    public string ScreenshotImageFormat { get; set; } = "png";

    /// <summary>有损格式的质量档位（1-100），PNG 无损忽略。默认 90（实测文档页 PSNR 47-50dB）。</summary>
    public int ScreenshotImageQuality { get; set; } = 90;

    /// <summary>
    /// 最近截图晾衣绳的触发区域（截图设置）：四条边各自独立启用、可调厚度/长度/位置，
    /// 重启后保留。默认上边、居中——与旧版顶部 32px 热区一致，只是可配。
    /// </summary>
    public ScreenshotTriggerSettings ScreenshotTrigger { get; set; } = new();

    /// <summary>
    /// Closing the main window hides it to the tray instead of exiting, so the global screenshot
    /// shortcut and the shelf stay available. The tray menu's 退出 is the explicit exit path.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Configured 网盘 accounts (网盘设置); secret values carry the <c>dpapi:</c> prefix
    /// at rest — see <see cref="NetdiskAccountPersistence"/>.</summary>
    public List<PersistedNetdiskAccount> NetdiskAccounts { get; set; } = new();

    /// <summary>
    /// Storage space quick uploads target (资源库/备份盘/相册). Empty = the account's default
    /// drive. Defaults to 资源库 so "upload" no longer lands in 备份盘's root.
    /// </summary>
    public string NetdiskUploadDrive { get; set; } = "资源库";

    /// <summary>
    /// Remote folder for quick uploads, e.g. <c>/来自分享</c>. Empty = the drive root. Missing
    /// folders are created on the first upload.
    /// </summary>
    public string NetdiskUploadDirectory { get; set; } = string.Empty;

    /// <summary>Deep copy: safe to hand to background work while the original keeps being edited.</summary>
    public AppSettings Clone() => new()
    {
        OutputSuffix = OutputSuffix,
        OutputMode = OutputMode,
        OutputDirectory = OutputDirectory,
        RemoveLinkedAnnotations = RemoveLinkedAnnotations,
        MaxConcurrency = MaxConcurrency,
        CheckUpdatesOnStartup = CheckUpdatesOnStartup,
        QuickSuffixes = new List<string>(QuickSuffixes),
        AutoNameByContent = AutoNameByContent,
        NameFromFirstPageOnly = NameFromFirstPageOnly,
        PreferredOcrEngine = PreferredOcrEngine,
        DefaultsNoticeVersion = DefaultsNoticeVersion,
        PreferredLayoutStage = PreferredLayoutStage,
        ScanDuplex = ScanDuplex,
        PerPageOrientationEnabled = PerPageOrientationEnabled,
        ContinuousScan = ContinuousScan,
        ScanDatasetExportEnabled = ScanDatasetExportEnabled,
        ScanColor = ScanColor,
        ScanOutputDirectory = ScanOutputDirectory,
        TransferOutputDirectory = TransferOutputDirectory,
        ScreenshotHotkey = ScreenshotHotkey,
        ScreenshotFullScreenHotkey = ScreenshotFullScreenHotkey,
        ScreenshotShelfHotkey = ScreenshotShelfHotkey,
        ScreenshotOutputDirectory = ScreenshotOutputDirectory,
        ScreenshotAutoCopy = ScreenshotAutoCopy,
        ScreenshotAutoSave = ScreenshotAutoSave,
        ScreenshotImageFormat = ScreenshotImageFormat,
        ScreenshotImageQuality = ScreenshotImageQuality,
        ScreenshotTrigger = ScreenshotTrigger.Clone(),
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        NetdiskAccounts = NetdiskAccounts.ToList(),
        NetdiskUploadDrive = NetdiskUploadDrive,
        NetdiskUploadDirectory = NetdiskUploadDirectory,
    };
}
