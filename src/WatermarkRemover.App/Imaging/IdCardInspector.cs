using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Imaging;

/// <summary>The two decisions the 身份证 template needs before composing: which photo is the
/// front (it goes on top) and what to call the file.</summary>
public sealed record IdCardPlan(IReadOnlyList<byte[]> OrderedImages, string? Name);

/// <summary>
/// Reads a pair of ID-card photos with the shared OCR engine: the front carries 姓名 and
/// 公民身份号码, the back carries 签发机关 / 有效期限. That is enough to put the front first and
/// to name the file 「姓名身份证」 — a couple of OCR calls, no page-level re-recognition after
/// composing. Anything unreadable or unrecognised simply keeps the gallery order and the share
/// title; this step must never block producing the file.
/// </summary>
public static class IdCardInspector
{
    /// <summary>OCR input cap: 1600 px keeps every printed glyph readable while halving the
    /// pixels the engine pays for versus a 300 dpi client scan.</summary>
    private const int OcrMaxSide = 1600;

    private static readonly string[] NameStopWords =
        { "性别", "民族", "出生", "住址", "公民", "号码", "身份", "签发", "有效", "期限", "姓名" };

    public static async Task<IdCardPlan> PrepareAsync(
        IReadOnlyList<byte[]> images,
        IOcrEngine engine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(engine);

        var faces = new List<Face>(images.Count);
        foreach (byte[] image in images)
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = await engine.RecognizeAsync(Prepare(image)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Recognition is an optimisation, not a prerequisite — an engine hiccup keeps
                // the original order and the title-based name.
                AppLog.Warn($"身份证识别失败，保留原顺序：{ex.Message}");
                lines = Array.Empty<string>();
            }

            faces.Add(new Face(lines));
            cancellationToken.ThrowIfCancellationRequested();
        }

        byte[][] ordered = images.ToArray();
        for (int i = 0; i + 1 < ordered.Length; i += 2)
        {
            // Front on top: swap only when the first of the pair is positively the back.
            if (!faces[i].IsBack || faces[i + 1].IsBack) continue;
            (ordered[i], ordered[i + 1]) = (ordered[i + 1], ordered[i]);
            (faces[i], faces[i + 1]) = (faces[i + 1], faces[i]);
        }

        string? name = faces.FirstOrDefault(f => f.IsFront && f.Name is not null)?.Name
            ?? faces.Select(f => f.Name).FirstOrDefault(n => n is not null);

        return new IdCardPlan(ordered, name);
    }

    /// <summary>Decodes, flattens onto white and caps the long side — the same shape the PDF
    /// renderer hands the OCR engine.</summary>
    private static byte[] Prepare(byte[] imageBytes)
    {
        using SKBitmap? decoded = SKBitmap.Decode(imageBytes);
        if (decoded is null) return imageBytes;

        using var flattened = new SKBitmap(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(decoded, 0, 0);
        }

        int longest = Math.Max(flattened.Width, flattened.Height);
        SKBitmap? scaled = null;
        try
        {
            SKBitmap source = flattened;
            if (longest > OcrMaxSide)
            {
                double factor = OcrMaxSide / (double)longest;
                scaled = new SKBitmap(
                    Math.Max(1, (int)Math.Round(flattened.Width * factor)),
                    Math.Max(1, (int)Math.Round(flattened.Height * factor)),
                    SKColorType.Bgra8888, SKAlphaType.Premul);
                if (!flattened.ScalePixels(scaled, new SKSamplingOptions(SKCubicResampler.Mitchell)))
                {
                    scaled.Dispose();
                    scaled = null;
                }
                else
                {
                    source = scaled;
                }
            }

            using SKImage image = SKImage.FromBitmap(source);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Per-photo OCR verdicts, computed once.</summary>
    private sealed class Face
    {
        public Face(IReadOnlyList<string> lines)
        {
            var norms = lines.Select(Normalize).Where(n => n.Length > 0).ToList();
            Name = norms.Select(ExtractInlineName).FirstOrDefault(n => n is not null)
                ?? ReadNameFromFollowingLine(norms);

            bool backMarker = norms.Any(n => n.Contains("签发机关", StringComparison.Ordinal) ||
                                             n.Contains("有效期限", StringComparison.Ordinal));
            bool frontMarker = norms.Any(n => n.Contains("公民身份号码", StringComparison.Ordinal)) ||
                               (Name is not null && norms.Any(n =>
                                   n.Contains("性别", StringComparison.Ordinal) ||
                                   n.Contains("民族", StringComparison.Ordinal) ||
                                   n.Contains("出生", StringComparison.Ordinal) ||
                                   n.Contains("住址", StringComparison.Ordinal)));

            IsBack = backMarker && !frontMarker;
            IsFront = frontMarker;
        }

        public bool IsBack { get; }

        public bool IsFront { get; }

        public string? Name { get; }

        private static string? ReadNameFromFollowingLine(IReadOnlyList<string> norms)
        {
            for (int i = 0; i < norms.Count; i++)
            {
                if (!norms[i].StartsWith("姓名", StringComparison.Ordinal)) continue;

                // The value may have been OCR'd as its own line right below the label.
                for (int j = i + 1; j <= Math.Min(i + 2, norms.Count - 1); j++)
                {
                    if (IsLikelyName(norms[j])) return norms[j];
                }
                break;
            }
            return null;
        }
    }

    /// <summary>Whitespace- and colon-free form of an OCR line.</summary>
    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : new string(text.Where(c => !char.IsWhiteSpace(c) && c is not '：' and not ':').ToArray());

    /// <summary>「姓名李文亨」→ 李文亨; a merged row「姓名李文亨性别男」stops at the next label.</summary>
    private static string? ExtractInlineName(string norm)
    {
        int at = norm.IndexOf("姓名", StringComparison.Ordinal);
        if (at < 0) return null;

        string tail = norm[(at + 2)..];
        int cut = tail.Length;
        foreach (string stop in NameStopWords)
        {
            int index = tail.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && index < cut) cut = index;
        }

        tail = tail[..cut];
        return IsLikelyName(tail) ? tail : null;
    }

    private static bool IsLikelyName(string candidate)
    {
        if (candidate.Length is < 2 or > 12) return false;
        if (NameStopWords.Any(candidate.Contains)) return false;
        return candidate.All(c => c == '·' || c is >= '\u4e00' and <= '\u9fff');
    }
}
