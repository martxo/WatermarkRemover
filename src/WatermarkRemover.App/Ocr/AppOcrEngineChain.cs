using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// The app's OCR engine chain, probed once per process. Registration order is priority order:
/// WeChat's local engine recognises Chinese documents best, then the bundled offline PP-OCR
/// models (for machines without WeChat), then the OCR built into Windows. Adding an engine =
/// implement <see cref="IOcrEngineFactory"/> and append a line here; selection, safe fallback
/// and diagnostics all follow automatically.
/// </summary>
internal static class AppOcrEngineChain
{
    public static OcrEngineChain Instance { get; } = new(new IOcrEngineFactory[]
    {
        new DelegateOcrEngineFactory("微信 OCR", () =>
        {
            if (WeChatOcrEngine.TryLocate(out WeChatOcrEngine? engine, out string reason))
                return (true, engine, string.Empty);
            return (false, null, reason);
        }),
        new DelegateOcrEngineFactory("PP-OCR", () =>
        {
            if (PpOcrEngine.TryLocate(out PpOcrEngine? engine, out string reason))
                return (true, engine, string.Empty);
            return (false, null, reason);
        }),
        new DelegateOcrEngineFactory("Windows 原生 OCR", () =>
        {
            // The WinRT OCR stack itself is missing/broken on some systems (REGDB_E_CLASSNOTREG);
            // the chain falls back to the terminal stub so the failure is reported per job,
            // never at startup.
            var windows = new WindowsMediaOcrEngine();
            return windows.IsAvailable
                ? (true, windows, string.Empty)
                : (false, null, "缺少 OCR 语言包");
        }),
    });
}
