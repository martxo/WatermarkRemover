using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Ocr;
using WatermarkRemover.Pdf;
using WatermarkRemover.Pdf.Rendering;

namespace WatermarkRemover.App;

/// <summary>
/// The one place where the application's object graph is built. The view model used to assemble
/// the feature list and the shared engine/renderer itself, which put composition in the UI layer:
/// swapping an implementation (another OCR engine, a test double) meant editing a 1300-line view
/// model. Construction lives here instead, so the graph is visible in one file and a headless host
/// can build the same features without the shell.
/// </summary>
public sealed class AppComposition
{
    private static readonly Lazy<AppComposition> LazyShared = new(() => new AppComposition());

    /// <summary>Process-wide instance used by the shell.</summary>
    public static AppComposition Shared => LazyShared.Value;

    public AppComposition()
    {
        PdfRenderer = new PdfiumPdfPageRenderer();
        OcrEngine = ResolveOcrEngine();
        Features = BuildFeatures(PdfRenderer, OcrEngine);
        Registry = new FeatureRegistry(Features);
    }

    /// <summary>Stateless page rasteriser shared by OCR, naming and the preview strip.</summary>
    public IPdfPageRenderer PdfRenderer { get; }

    /// <summary>The OCR engine chosen by the engine chain (WeChat → PP-OCR → Windows).</summary>
    public IOcrEngine OcrEngine { get; }

    /// <summary>Features in registration (= sidebar) order.</summary>
    public IReadOnlyList<IFeature> Features { get; }

    public FeatureRegistry Registry { get; }

    /// <summary>Logs which OCR engines are available, then hands back the selected one.</summary>
    private static IOcrEngine ResolveOcrEngine()
    {
        foreach (ProbeLink<IOcrEngine> link in AppOcrEngineChain.Instance.Links)
        {
            if (link.Available)
                AppLog.Info($"OCR 引擎：{link.Name}");
            else
                AppLog.Info($"未启用 {link.Name}：{link.Reason}");
        }

        return AppOcrEngineChain.Current;
    }

    private static IReadOnlyList<IFeature> BuildFeatures(
        IPdfPageRenderer renderer, IOcrEngine ocrEngine) => new IFeature[]
    {
        // Scan first: the app opens straight onto the scan page, so a walk-up user can scan
        // without touching navigation. Registration order drives both the sidebar and the
        // default selection.
        new ScanFeature(),
        // 快速搜索 sits next to the default page: it is a tool, not a processor (CanHandle=false),
        // so it never interferes with first-match routing in FindForFile.
        new QuickSearchFeature(),
        new WatermarkFeature(
            new PdfDocumentSource(),
            new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            renderer,
            ocrEngine,
            orientPage: image => new PdfOrientationDetector(ocrEngine).OrientAsync(image),
            trace: AppLog.Trace),
        new OcrFeature(renderer, ocrEngine, trace: AppLog.Trace,
            imageLoader: ImageOcrInput.LoadAsOcrPng),
        new NetdiskFeature(),
        new LanTransferFeature(),
    };
}
