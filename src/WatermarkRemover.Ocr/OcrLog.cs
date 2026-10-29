namespace WatermarkRemover.Ocr;

/// <summary>
/// Optional log sink for the OCR library. Engines and layout stages are library code and must not
/// reference the app's logger directly; the host points this at its own log once at startup.
/// When unset (tests, CLI consumers), messages are simply dropped.
/// </summary>
public static class OcrLog
{
    public static Action<string>? Info { get; set; }

    internal static void Write(string message) => Info?.Invoke(message);
}
