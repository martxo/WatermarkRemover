using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Ocr;
using WatermarkRemover.Ocr.Layout;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// The app's OCR engine chain, probed once per process. Registration order is priority order:
/// WeChat's local engine recognises Chinese documents best, then the bundled offline PP-OCR
/// models (for machines without WeChat), then the OCR built into Windows. Adding an engine =
/// implement <see cref="IProbeFactory{TProduct}"/> and append a line here (name + description + probe);
/// selection, safe fallback, the settings OCR section and diagnostics all follow automatically.
/// </summary>
internal static class AppOcrEngineChain
{
    static AppOcrEngineChain() => OcrLog.Info = AppLog.Info;

    public static OcrEngineChain Instance { get; } = new(new IProbeFactory<IOcrEngine>[]
    {
        new DelegateProbeFactory<IOcrEngine>(
            "微信 OCR",
            () =>
            {
                if (WeChatOcrEngine.TryLocate(out WeChatOcrEngine? engine, out string reason))
                    return (true, engine, string.Empty);
                return (false, null, reason);
            },
            "微信内置的本地引擎，中文证照 / 票据识别效果最佳；识别时不联网，但需要本机已安装微信。"),
        new DelegateProbeFactory<IOcrEngine>(
            "PP-OCR",
            () =>
            {
                if (PpOcrEngine.TryLocate(out PpOcrEngine? engine, out string reason))
                    return (true, engine, string.Empty);
                return (false, null, reason);
            },
            "随软件内置的离线引擎（约 21MB 模型），不依赖微信和系统组件，任何 Windows 都能用。"),
        new DelegateProbeFactory<IOcrEngine>(
            "Windows 原生 OCR",
            () =>
            {
                // The WinRT OCR stack itself is missing/broken on some systems (REGDB_E_CLASSNOTREG);
                // the chain falls back to the terminal stub so the failure is reported per job,
                // never at startup.
                var windows = new WindowsMediaOcrEngine();
                return windows.IsAvailable
                    ? (true, windows, string.Empty)
                    : (false, null, "缺少 OCR 语言包");
            },
            "Windows 自带的 OCR，需要系统安装中文语言包；精简 / N 版系统可能缺失组件。"),
    });

    /// <summary>
    /// Stable proxy handed to every feature. The settings OCR section changes the preference at
    /// runtime by switching this proxy's target — features keep their one reference and simply
    /// forward the next recognition call to the newly selected engine.
    /// </summary>
    public static SwitchableOcrEngine Current { get; } = new(Instance.Engine);

    /// <summary>
    /// Composes the two settings preferences — layout block × text engine — into one
    /// <see cref="PipelineOcrEngine"/> and switches the stable proxy to it. Empty names keep
    /// the automatic priority heads, so a fresh or stale setting can never leave the app
    /// without a recogniser.
    /// </summary>
    public static void ApplyPipeline(string? layoutName, string? engineName)
    {
        ILayoutStage stage = OcrLayoutStageChain.Instance.Select(layoutName);
        IOcrEngine engine = Instance.Select(engineName);
        var pipeline = new PipelineOcrEngine(stage, engine, CropForOcr, trace: AppLog.Trace);
        Current.SwitchTo(pipeline);
        AppLog.Info($"管线：{pipeline.DisplayName}" +
            (stage.IsPassthrough ? "（整图直通）" : string.Empty) +
            DescribePreference(layoutName, engineName));
    }

    private static string DescribePreference(string? layoutName, string? engineName)
    {
        string layout = string.IsNullOrWhiteSpace(layoutName) ? "版面自动" : $"版面「{layoutName}」";
        string engine = string.IsNullOrWhiteSpace(engineName) ? "引擎自动" : $"引擎「{engineName}」";
        return $"（{layout}，{engine}）";
    }

    /// <summary>Cuts a normalized region out of the encoded page (PNG in, PNG out) for the
    /// pipeline's per-region recognition pass.</summary>
    private static byte[] CropForOcr(byte[] imageBytes, RectD region)
    {
        using SKBitmap source = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("无法解码输入图像。");

        int x = Math.Clamp((int)Math.Floor(region.X * source.Width), 0, source.Width - 1);
        int y = Math.Clamp((int)Math.Floor(region.Y * source.Height), 0, source.Height - 1);
        int w = Math.Clamp((int)Math.Ceiling(region.Width * source.Width), 1, source.Width - x);
        int h = Math.Clamp((int)Math.Ceiling(region.Height * source.Height), 1, source.Height - y);

        using var cropped = new SKBitmap(w, h);
        if (!source.ExtractSubset(cropped, new SKRectI(x, y, x + w, y + h)))
            throw new InvalidOperationException("图像区域裁剪失败。");

        using SKImage image = SKImage.FromBitmap(cropped);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
