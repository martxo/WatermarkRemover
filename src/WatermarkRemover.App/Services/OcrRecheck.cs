using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Independent second opinion on a naming OCR pass: upscale the page image and recognise it
/// again, then check whether the first pass's name still shows up and how close the two texts
/// are. A page the engine cannot read consistently is flagged for a human instead of being
/// trusted. Pure check — it never changes the name and never throws; a broken recheck simply
/// reports "不可用" and leaves the first result alone.
/// </summary>
internal sealed class OcrRecheck
{
    /// <summary>Upscale factor for the second read (2× is where thin strokes separate).</summary>
    private const double MaxUpscale = 2.0;

    /// <summary>Long-side cap for the enlarged image: beyond this the OCR gets slower without
    /// reading better, and a 4000px scan doubled would cost hundreds of MB.</summary>
    private const int MaxLongSide = 3600;

    /// <summary>Levenshtein is O(n·m); naming pages are far shorter than this, the cap only
    /// protects against a runaway OCR dump.</summary>
    private const int MaxCompareChars = 4000;

    private readonly IOcrEngine _ocr;

    public OcrRecheck(IOcrEngine ocr) =>
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));

    /// <summary>Verdict + the sentence shown on the row and in the report.</summary>
    public sealed record Outcome(bool NeedsReview, string Evidence);

    /// <param name="png">The oriented page image the first pass recognised (PNG bytes).</param>
    /// <param name="firstPassText">Text the first pass used (full-page lines, or the located name).</param>
    /// <param name="personName">First pass's name; placeholder names ("谁…") are not required to
    /// reappear, because they are deliberately generated rather than read.</param>
    public async Task<Outcome> CheckAsync(byte[] png, string firstPassText, string? personName)
    {
        try
        {
            (byte[] enlarged, double factor) = Upscale(png);
            IReadOnlyList<string> lines = await _ocr.RecognizeAsync(enlarged).ConfigureAwait(false);
            string second = string.Join("\n", lines);

            string firstNorm = Normalize(firstPassText);
            string secondNorm = Normalize(second);
            if (secondNorm.Length < 2)
                return new Outcome(true, $"放大 {factor:0.0}× 复查几乎读不出文字（页面可能不清晰）");

            bool generatedName = string.IsNullOrWhiteSpace(personName)
                || personName.StartsWith('谁');
            string nameNorm = Normalize(personName ?? string.Empty);
            bool nameFound = generatedName || nameNorm.Length < 2 ||
                secondNorm.Contains(nameNorm, StringComparison.Ordinal);

            double similarity = Similarity(firstNorm, secondNorm);
            if (!nameFound)
            {
                return new Outcome(true,
                    $"放大 {factor:0.0}× 复查没读到名称「{personName}」（文本相似度 {similarity:P0}），" +
                    "可能把字认成了别的字");
            }

            if (firstNorm.Length >= 20 && similarity < 0.6)
            {
                return new Outcome(true,
                    $"放大 {factor:0.0}× 复查文本差异大（相似度 {similarity:P0}），建议人工核实");
            }

            return new Outcome(false, $"放大 {factor:0.0}× 复查一致（相似度 {similarity:P0}）");
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：放大复查不可用，跳过", ex);
            return new Outcome(false, "复查不可用（已跳过）");
        }
    }

    private static (byte[] Bytes, double Factor) Upscale(byte[] png)
    {
        using var input = new MemoryStream(png);
        BitmapFrame source = BitmapDecoder
            .Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            .Frames[0];

        double factor = Math.Min(MaxUpscale,
            (double)MaxLongSide / Math.Max(source.PixelWidth, source.PixelHeight));
        if (factor < 1.1) factor = 1.1;

        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var output = new MemoryStream();
        encoder.Save(output);
        return (output.ToArray(), factor);
    }

    /// <summary>Whitespace/punctuation carry no meaning here and line order can change between
    /// engines; keep letters/digits only so "张 三" and "张三" compare equal.</summary>
    private static string Normalize(string text)
    {
        int length = Math.Min(text.Length, MaxCompareChars);
        Span<char> buffer = length <= 1024 ? stackalloc char[length] : new char[length];
        int count = 0;
        foreach (char ch in text.AsSpan(0, length))
        {
            if (char.IsLetterOrDigit(ch)) buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
    }

    /// <summary>1 - normalised edit distance, i.e. how much of the text agrees.</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
