using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// Text recognition through the OCR engine built into Windows 10+ — zero footprint, zero
/// dependencies, fully offline. Recognition quality depends on the language packs installed on
/// the machine; Simplified Chinese is preferred when available.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public WindowsMediaOcrEngine() => _engine = TryCreateEngine();

    public string DisplayName => "Windows原生OCR";

    /// <summary>Whether a usable OCR engine exists on this machine.</summary>
    public bool IsAvailable => _engine is not null;

    public async Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (_engine is null)
            throw new InvalidOperationException("此系统没有可用的 OCR 语言包，请在 Windows 设置中添加中文语言。");

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        OcrResult result = await _engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l => l.Text).ToList();
    }

    private static OcrEngine? TryCreateEngine()
    {
        // Prefer Simplified Chinese (the primary audience of this tool), then whatever the user
        // has installed, then any language the system can offer.
        return OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages
                .Select(OcrEngine.TryCreateFromLanguage)
                .FirstOrDefault(engine => engine is not null);
    }
}
