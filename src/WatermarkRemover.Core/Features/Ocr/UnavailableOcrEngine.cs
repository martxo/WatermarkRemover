namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Terminal fallback of the engine chain: every call fails with the full probe history, so the
/// user sees why this machine has no OCR instead of a bare crash at startup.
/// </summary>
public sealed class UnavailableOcrEngine : IOcrEngine
{
    private readonly string _reason;

    public UnavailableOcrEngine(string reason) => _reason = reason;

    public string DisplayName => "无可用OCR引擎";

    public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
        throw new InvalidOperationException($"此电脑没有可用的 OCR 引擎。\n{_reason}");
}
