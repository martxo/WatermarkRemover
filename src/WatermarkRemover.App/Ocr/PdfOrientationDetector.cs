using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// Detects which way a scanned page is facing. Two judgement channels, picked by engine
/// capability:
///
/// 1. Geometry (engines that report line bounds — WeChat / PP-OCR / Windows all do): upright
///    text lines are wide-and-short boxes stacked top-down; a sideways page's lines are tall
///    boxes, an upside-down page's lines run bottom-up. This judgement is immune to how good
///    the engine is at reading rotated text — an engine that reads every rotation equally well
///    (WeChat does) still reports the same boxes, so the geometry still separates the turns.
///
/// 2. Text volume (engines without bounds): four readings, most characters wins. Kept as the
///    fallback; older judgement, weaker against rotation-robust engines.
///
/// Duplex back sides (the common upside-down case) resolve reliably through either channel.
/// </summary>
public sealed class PdfOrientationDetector
{
    /// <summary>The upright reading must beat the current orientation by this factor before
    /// we dare touch the file — noise on an already-upright page must never rotate it.</summary>
    internal const double WinFactor = 1.25;

    /// <summary>Text-volume voting needs this many characters before a turn is believed.</summary>
    internal const int MinWinningChars = 20;

    /// <summary>Geometry needs at least this many positioned lines before a turn is believed.</summary>
    internal const int MinGeometryLines = 3;

    /// <summary>A geometric reading must score at least this (wide-ratio × order-agreement)
    /// to count as decisive — sloppy boxes (stamps, tables) must not rotate the file.</summary>
    internal const double MinGeometryScore = 0.5;

    private readonly IOcrEngine _engine;

    public PdfOrientationDetector(IOcrEngine engine) => _engine = engine;

    /// <summary>
    /// Returns how many clockwise quarter turns the page needs to read upright (0 = already
    /// correct). Any engine failure degrades to "leave it alone" — auto-orientation is a
    /// convenience and must never turn into an error the user has to dismiss.
    /// </summary>
    public async Task<int> DetectClockwiseStepsAsync(byte[] pagePng, CancellationToken cancellationToken = default)
    {
        var scores = new Dictionary<int, double>(4);
        for (int steps = 0; steps < 4; steps++)
        {
            byte[] candidate = steps == 0 ? pagePng : Rotate(pagePng, steps);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                scores[steps] = _engine is IBoundedOcrEngine bounded
                    ? await GeometryScoreAsync(bounded, candidate)
                    : await TextVolumeScoreAsync(candidate);
            }
            catch (InvalidOperationException)
            {
                return 0; // no usable OCR on this machine — silently keep the page as scanned
            }
        }
        return PickBestTurn(scores, minWinningScore: _engine is IBoundedOcrEngine ? MinGeometryScore : MinWinningChars);
    }

    /// <summary>Geometry channel: read the candidate upright and score its line-box layout.</summary>
    private async Task<double> GeometryScoreAsync(IBoundedOcrEngine bounded, byte[] candidate)
    {
        IReadOnlyList<RecognizedLine> lines = await bounded.RecognizeWithBoundsAsync(candidate);
        return GeometryScore(lines);
    }

    /// <summary>Text-volume channel: plain recognition, count what came back.</summary>
    private async Task<double> TextVolumeScoreAsync(byte[] candidate)
    {
        IReadOnlyList<string> lines = await _engine.RecognizeAsync(candidate);
        return lines.Sum(line => (double)line.Count(char.IsLetterOrDigit));
    }

    /// <summary>Four per-turn scores in; the turn to apply out. Internal so tests pin the
    /// voting rule without any OCR or image work. A winner must clear the channel's floor AND
    /// beat the current orientation by <see cref="WinFactor"/>.</summary>
    internal static int PickBestTurn(IReadOnlyDictionary<int, double> scores, double minWinningScore)
    {
        double straight = scores.GetValueOrDefault(0);
        (int Steps, double Score) best = (0, straight);
        foreach (var (steps, score) in scores)
        {
            if (score > best.Score) best = (steps, score);
        }

        bool decisive = best.Score >= straight * WinFactor && best.Score >= minWinningScore;
        return decisive ? best.Steps : 0;
    }

    /// <summary>
    /// How much a page of positioned lines looks like upright horizontal text: the share of
    /// lines that are wide relative to their height (≥1.5:1) times the share of adjacent line
    /// pairs whose tops descend in reading order. Upright ⇒ ≈1.0; sideways ⇒ wide≈0; upside
    /// down ⇒ ordered≈0. Bounded 0..1; needs <see cref="MinGeometryLines"/> positioned lines.
    /// </summary>
    internal static double GeometryScore(IReadOnlyList<RecognizedLine> lines)
    {
        var boxed = lines
            .Where(l => l.Bounds is { } b && b.Width > 0 && b.Height > 0)
            .Select(l => l.Bounds!.Value)
            .ToList();
        if (boxed.Count < MinGeometryLines) return 0;

        double wide = boxed.Count(b => b.Width / b.Height >= 1.5) / (double)boxed.Count;
        int pairs = 0, descending = 0;
        for (int i = 1; i < boxed.Count; i++)
        {
            pairs++;
            if (boxed[i].Y >= boxed[i - 1].Y) descending++;
        }
        double ordered = descending / (double)pairs;
        return wide * ordered;
    }

    /// <summary>Quarter-turn clockwise re-encode, in-process via Skia (no file, no GDI).</summary>
    private static byte[] Rotate(byte[] png, int quarterTurns)
    {
        using var source = SkiaSharp.SKBitmap.Decode(png);
        if (source is null) return png;

        bool swap = quarterTurns % 2 == 1;
        int width = swap ? source.Height : source.Width;
        int height = swap ? source.Width : source.Height;
        using var target = new SkiaSharp.SKBitmap(width, height, source.ColorType, source.AlphaType);
        using (var canvas = new SkiaSharp.SKCanvas(target))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees(90 * quarterTurns);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(target);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
