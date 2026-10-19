using WatermarkRemover.Core.Features.Layout;

namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Stable proxy handed to features while the actual recogniser may change at runtime (the
/// settings OCR section picks the preferred engine). Features keep one reference for their
/// whole lifetime; switching swaps the delegate target in place, so the next recognition call
/// goes to the new engine without any feature being rebuilt.
/// </summary>
public sealed class SwitchableOcrEngine : IOcrEngine, IBoundedOcrEngine
{
    private volatile IOcrEngine _current;

    public SwitchableOcrEngine(IOcrEngine initial) =>
        _current = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>The engine every call is currently forwarded to.</summary>
    public IOcrEngine Current => _current;

    /// <summary>Points the proxy at another engine (e.g. after the user changes the preference).</summary>
    public void SwitchTo(IOcrEngine engine) =>
        _current = engine ?? throw new ArgumentNullException(nameof(engine));

    public string DisplayName => _current.DisplayName;

    public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
        _current.RecognizeAsync(imageBytes);

    /// <summary>Forwarded when the current engine can report bounds; an engine without the
    /// capability makes the call fail loudly so callers can degrade to the plain-text path.</summary>
    public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes) =>
        _current is IBoundedOcrEngine bounded
            ? bounded.RecognizeWithBoundsAsync(imageBytes)
            : throw new InvalidOperationException("当前 OCR 引擎无法提供文字位置信息。");
}
