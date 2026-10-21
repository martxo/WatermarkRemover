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

/// <summary>User preferences, persisted as JSON alongside the application.</summary>
public sealed class AppSettings
{
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
    public List<string> QuickSuffixes { get; set; } = new() { "身份证", "户口本", "房产证", "征信", "执照" };

    /// <summary>
    /// Name watermark-removal outputs after the document's content (e.g. 林孝明执照.pdf) when
    /// the document type and customer name can be recognised. Falls back to
    /// <see cref="OutputSuffix"/> naming otherwise.
    /// </summary>
    public bool AutoNameByContent { get; set; } = true;

    /// <summary>
    /// Probe only the first page for content-based naming on multi-page documents (on by
    /// default): the type and the customer name almost always sit on page one, and serial OCR
    /// passes over a long document hold the pipeline for seconds. Off = probe up to four
    /// pages, for scans whose first face is a cover or a blank duplex back.
    /// </summary>
    public bool NameFromFirstPageOnly { get; set; } = true;

    /// <summary>
    /// Name of the OCR engine the user pinned in the settings OCR section (a factory name from
    /// the engine chain). Empty = pick the best available engine automatically. A preference
    /// whose engine is unavailable falls back to the automatic chain at runtime.
    /// </summary>
    public string PreferredOcrEngine { get; set; } = string.Empty;

    /// <summary>
    /// Name of the layout block (first pipeline stage) the user pinned in the settings OCR
    /// section (a factory name from the layout stage chain). Empty = full-image passthrough,
    /// i.e. the pre-pipeline behaviour. A preference whose block is unavailable falls back to
    /// the automatic chain at runtime.
    /// </summary>
    public string PreferredLayoutStage { get; set; } = string.Empty;

    /// <summary>Scan page layout (扫描页): 0 = 单面, 1 = 双面长边, 2 = 双面短边.</summary>
    public int ScanDuplex { get; set; }

    /// <summary>Scan colour mode (扫描色彩): 0 = 自动, 1 = 彩色, 2 = 灰度, 3 = 黑白. Defaults to
    /// 黑白 — text documents scan ~2x faster and produce far smaller, crisper PDFs.</summary>
    public int ScanColor { get; set; } = 3;

    /// <summary>Where scanned PDFs are written (扫描设置 → 输出目录). Empty = the Pictures
    /// folder, which stays the default so an upgrade never moves existing scans elsewhere.
    /// Independent of the watermark feature's OutputDirectory — scans have no source file.</summary>
    public string ScanOutputDirectory { get; set; } = string.Empty;

    /// <summary>Where phone uploads and share-link PDFs land (手机传文件保存位置). Empty = a
    /// 「手机传文件」 folder on the Desktop, the pre-setting default.</summary>
    public string TransferOutputDirectory { get; set; } = string.Empty;

    /// <summary>Configured 网盘 accounts (网盘设置); secret values carry the <c>dpapi:</c> prefix
    /// at rest — see <see cref="NetdiskAccountPersistence"/>.</summary>
    public List<PersistedNetdiskAccount> NetdiskAccounts { get; set; } = new();

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
        PreferredLayoutStage = PreferredLayoutStage,
        ScanDuplex = ScanDuplex,
        ScanColor = ScanColor,
        ScanOutputDirectory = ScanOutputDirectory,
        TransferOutputDirectory = TransferOutputDirectory,
        NetdiskAccounts = NetdiskAccounts.ToList(),
    };
}
