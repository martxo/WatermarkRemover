using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Naming;
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

    /// <summary>Long-side cap for the enlarged image. The project's OCR input contract is
    /// 2400px (see ImageOcrInput); going beyond it makes the engine shrink the page internally
    /// and small header cells stop being read at all — that is exactly how the first version
    /// produced "放大复查没读到名称" on perfectly clear credit reports.</summary>
    private const int MaxLongSide = 2400;

    /// <summary>Levenshtein is O(n·m); naming pages are far shorter than this, the cap only
    /// protects against a runaway OCR dump.</summary>
    private const int MaxCompareChars = 4000;

    private readonly IOcrEngine _ocr;

    public OcrRecheck(IOcrEngine ocr) =>
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));

    /// <summary>Verdict + the sentence shown on the row and in the report.</summary>
    public sealed record Outcome(bool NeedsReview, string Evidence);

    /// <summary>
    /// Credit report: the first pass reads the 姓名 value cell at 3× zoom (the header table is
    /// tiny). Re-read the SAME cell at another zoom instead of the whole page — a full-page
    /// second pass is a different task and used to fail on clear reports. Comparing two reads
    /// of the same region is what actually tells us the characters are unambiguous.
    /// </summary>
    public async Task<Outcome> CheckCreditNameAsync(byte[] png, string personName)
    {
        if (_ocr is not IBoundedOcrEngine)
            return new Outcome(false, "复查跳过：当前引擎不支持坐标");

        try
        {
            string? second = await CreditReportNameLocator
                .TryLocateNameAsync(png, _ocr, trace: null, cellZoom: 4).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(second))
                return new Outcome(true,
                    $"放大 4× 复查姓名值格没读到字（首轮「{personName}」），页面可能不清晰");

            if (!string.Equals(Normalize(second), Normalize(personName), StringComparison.Ordinal))
            {
                return new Outcome(true,
                    $"两轮识别不一致：首轮「{personName}」，放大复查「{second}」");
            }

            return new Outcome(false, $"放大 4× 复查姓名一致（{second}）");
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：征信姓名放大复查不可用，跳过", ex);
            return new Outcome(false, "复查不可用（已跳过）");
        }
    }

    /// <param name="png">The oriented page image the first pass recognised (PNG bytes).</param>
    /// <param name="firstPassText">Text the first pass used (full-page lines, or the located name).</param>
    /// <param name="personName">First pass's name; placeholder names ("谁…") are not required to
    /// reappear, because they are deliberately generated rather than read.</param>
    public async Task<Outcome> CheckAsync(byte[] png, string firstPassText, string? personName)
    {
        try
        {
            (byte[] enlarged, double factor) = Upscale(png);
            if (enlarged.Length == 0)
                return new Outcome(false, "复查跳过：原图已达识别上限");
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
        // Already at the engine's input limit: a second read of the identical image proves
        // nothing (same input, same output) — report "skipped" instead of a fake verdict.
        if (factor < 1.05) return (Array.Empty<byte>(), 1.0);

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
