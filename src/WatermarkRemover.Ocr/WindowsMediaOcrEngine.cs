using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Ocr;

/// <summary>
/// Text recognition through the OCR engine built into Windows 10+ — zero footprint, zero
/// dependencies, fully offline. Recognition quality depends on the language packs installed on
/// the machine; Simplified Chinese is preferred when available.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine, IBoundedOcrEngine
{
    private readonly OcrEngine? _engine;

    /// <summary>WinRT's OcrEngine is not documented as thread-safe and concurrent RecognizeAsync
    /// calls do misbehave — the same cross-talk class of bug as the pdfium renderer. The other
    /// engines in the chain gate their native calls; this one serialises the same way.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsMediaOcrEngine() => _engine = TryCreateEngine();

    public string DisplayName => "Windows原生OCR";

    /// <summary>Whether a usable OCR engine exists on this machine.</summary>
    public bool IsAvailable => _engine is not null;

    public async Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
    {
        (OcrResult result, _, _) = await RunAsync(imageBytes).ConfigureAwait(false);
        return result.Lines.Select(l => l.Text).ToList();
    }

    /// <summary>Line bounds are the union of the line's word rectangles, normalized to the
    /// decoded bitmap — this is what drives the anchor-based layout blocks.</summary>
    public async Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes)
    {
        (OcrResult result, int width, int height) = await RunAsync(imageBytes).ConfigureAwait(false);
        return result.Lines
            .Select(line => new RecognizedLine(line.Text, LineBounds(line, width, height)))
            .ToList();
    }

    private async Task<(OcrResult Result, int Width, int Height)> RunAsync(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (_engine is null)
            throw new InvalidOperationException("此系统没有可用的 OCR 语言包，请在 Windows 设置中添加中文语言。");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
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
            return (result, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RectD? LineBounds(OcrLine line, int width, int height)
    {
        if (line.Words.Count == 0 || width <= 0 || height <= 0) return null;

        double minX = line.Words.Min(w => w.BoundingRect.X);
        double minY = line.Words.Min(w => w.BoundingRect.Y);
        double maxX = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
        double maxY = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);

        double x = Math.Clamp(minX / width, 0, 1);
        double y = Math.Clamp(minY / height, 0, 1);
        double w = Math.Clamp((maxX - minX) / width, 0, 1 - x);
        double h = Math.Clamp((maxY - minY) / height, 0, 1 - y);
        return w <= 0 || h <= 0 ? null : new RectD(x, y, w, h);
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
