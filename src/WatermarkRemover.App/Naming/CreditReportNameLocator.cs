using System.Text.RegularExpressions;
using SkiaSharp;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.App.Naming;

/// <summary>
/// Fixed-region name locator for the personal credit report (detailed edition). The report's
/// layout is fixed by the PBOC: on a correctly oriented page the「被查询者姓名」header table
/// always sits in the top band, and the subject's name is the cell directly BELOW the label.
/// Instead of OCR-ing the whole page and guessing a name out of shredded text order (duplex
/// and watermark-pressed 2-up/4-up sheets scramble lines), this locator crops just the top
/// band, finds the label box geometrically, crops the value cell below it and recognises only
/// that tiny crop — fast, and immune to reading-order chaos. Anything that does not match the
/// fixed layout (another document, a rotated page) yields null and the caller falls back to
/// the generic text pipeline. Because the ADF presents a landscape page sideways on a portrait
/// canvas (and duplex back sides upside down), the top band is probed per plausible orientation
/// until the label appears — the fixed-region idea works on whatever orientation the scanner
/// actually produced, without a separate orient pass.
/// </summary>
public static class CreditReportNameLocator
{
    /// <summary>Fraction of the page height probed for the header table. On real reports the
    /// label row sits at ~27% of page height; 45% leaves room for letterhead variants.</summary>
    private const double ProbeBandHeight = 0.45;

    /// <summary>Value-cell crop in units of the label box (measured on a real report: the name
    /// cell is dead-centre below the label, its top edge ≈1.3 label heights below the label's
    /// bottom, its bottom edge ≈2.1). The horizontal margin stays well short of the next
    /// column (证件类型), so neighbouring cells cannot leak into the crop.</summary>
    private const double CellPadX = 0.35;
    private const double CellTopGap = 0.15;
    private const double CellBottomGap = 2.4;

    /// <summary>Zoom factor for the tiny cell crop — small images recognise poorly.</summary>
    private const int CellZoom = 3;

    /// <summary>Band probes per canvas aspect, in try order. A landscape report fed through
    /// the ADF comes out sideways on a portrait canvas (90/270); an upright landscape page
    /// can only be upside down (duplex back side, 180). Bounded: at most 3 probes per page.</summary>
    private static IReadOnlyList<int> OrientationsFor(int width, int height) =>
        height > width ? new[] { 0, 90, 270 } : new[] { 0, 180 };

    /// <summary>Label words that mark a non-name cell (a neighbouring column leaking in).</summary>
    private static readonly string[] CellStopWords = { "证件", "号码", "查询", "机构", "原因", "姓名", "身份证" };

    /// <summary>
    /// Attempts to read the credit report subject's name from the rendered page image.
    /// Returns null when the engine cannot report positions, the header label is absent
    /// (another document or a rotated page) or the value cell yields no usable name.
    /// </summary>
    public static async Task<string?> TryLocateNameAsync(
        byte[] pageImage, IOcrEngine engine, Action<string, Exception?>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(pageImage);
        ArgumentNullException.ThrowIfNull(engine);

        if (engine is not IBoundedOcrEngine bounded)
        {
            trace?.Invoke("征信定位：当前引擎不支持坐标，跳过固定区域定位", null);
            return null;
        }

        try
        {
            using var page = SKBitmap.Decode(pageImage);
            if (page is null)
            {
                trace?.Invoke("征信定位：页面图像解码失败", null);
                return null;
            }

            // The scan can present the page sideways (ADF feed) or upside down (duplex back):
            // probe the top band of each plausible orientation until the label appears.
            foreach (int degrees in OrientationsFor(page.Width, page.Height))
            {
                using SKBitmap? rotated = degrees == 0 ? null : Rotate(page, degrees);
                SKBitmap oriented = rotated ?? page;
                string? name = await TryLocateInOrientationAsync(
                    bounded, engine, oriented, degrees, trace).ConfigureAwait(false);
                if (name is not null) return name;
            }

            trace?.Invoke("征信定位：各方向顶部条带均未找到「被查询者姓名」标签，交回常规识别", null);
            return null;
        }
        catch (Exception ex)
        {
            trace?.Invoke("征信定位：过程异常，交回常规识别", ex);
            return null;
        }
    }

    /// <summary>One orientation probe: crop the top band, find the label, read the cell below
    /// it. Returns the cleaned name, or null (the caller tries the next orientation).</summary>
    private static async Task<string?> TryLocateInOrientationAsync(
        IBoundedOcrEngine bounded,
        IOcrEngine engine,
        SKBitmap oriented,
        int degrees,
        Action<string, Exception?>? trace)
    {
        // 1. Probe pass on the top band only — a fraction of the full-page pixels.
        int bandHeight = Math.Max(1, (int)(oriented.Height * ProbeBandHeight));
        using var band = Crop(oriented, 0, 0, oriented.Width, bandHeight);
        IReadOnlyList<RecognizedLine> lines =
            await bounded.RecognizeWithBoundsAsync(Encode(band)).ConfigureAwait(false);

        // 2. The label box; topmost-leftmost wins (the first report page on a multi-up sheet).
        RecognizedLine? anchor = FindAnchor(lines);
        if (anchor?.Bounds is not { } label)
        {
            trace?.Invoke($"征信定位：{degrees}° 顶部条带未找到「被查询者姓名」标签", null);
            return null;
        }

        // 3. Crop the value cell below the label, zoom it and recognise just that crop.
        RectD cell = CellRect(label);
        using var cellBitmap = Crop(
            band,
            (int)(cell.X * band.Width), (int)(cell.Y * band.Height),
            Math.Max(1, (int)(cell.Width * band.Width)),
            Math.Max(1, (int)(cell.Height * band.Height)));
        using var zoomed = Scale(cellBitmap, CellZoom);
        IReadOnlyList<string> cellLines =
            await engine.RecognizeAsync(Encode(zoomed)).ConfigureAwait(false);

        string? name = CleanCellValue(cellLines);
        trace?.Invoke(
            name is null ? $"征信定位：{degrees}° 值格未识别出姓名" : $"征信定位：{degrees}° 值格识别 → {name}",
            null);
        return name;
    }

    /// <summary>True when the line is the report's name label, tolerating OCR noise (inserted
    /// spaces, colons, a merged next cell: 被查询者姓名证件类型).</summary>
    public static bool IsAnchorLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string norm = new(text.Where(c => !char.IsWhiteSpace(c) && c is not '：' and not ':').ToArray());
        return norm.StartsWith("被查询者姓名", StringComparison.Ordinal);
    }

    /// <summary>The topmost-leftmost anchor line — the first report page on a multi-up sheet.</summary>
    public static RecognizedLine? FindAnchor(IReadOnlyList<RecognizedLine> lines)
    {
        RecognizedLine? best = null;
        foreach (RecognizedLine line in lines)
        {
            if (line.Bounds is not { } bounds || bounds.Width <= 0 || bounds.Height <= 0) continue;
            if (!IsAnchorLabel(line.Text)) continue;
            if (best?.Bounds is not { } current ||
                bounds.Y < current.Y ||
                (bounds.Y == current.Y && bounds.X < current.X))
                best = line;
        }
        return best;
    }

    /// <summary>The name cell: directly below the label, label-width centred. Derived from a
    /// real report's measurements (label X=0.072 Y=0.273 W=0.043 H=0.013 → name X=0.085
    /// Y=0.302; the next column 身份证 at X=0.148 stays outside the crop). Bounds are image
    /// coordinates (Y = top edge), so edges are computed manually — RectD's Bottom/Top
    /// properties mean PDF user space, not this.</summary>
    public static RectD CellRect(RectD label)
    {
        double labelBottom = label.Y + label.Height;
        double padX = label.Width * CellPadX;
        double top = labelBottom + label.Height * CellTopGap;
        double bottom = labelBottom + label.Height * CellBottomGap;
        return new RectD(label.X - padX, top, label.Width + 2 * padX, bottom - top);
    }

    /// <summary>First CJK run of the cell text, cut at the first label word — the name. A
    /// neighbouring cell leaking into the crop (庞结身份证) truncates at 身份证; pure numbers
    /// and bare label words yield null.</summary>
    public static string? CleanCellValue(IReadOnlyList<string> lines)
    {
        string text = string.Concat(lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            .Replace(" ", "").Replace("　", "");
        foreach (Match m in CjkRun.Matches(text))
        {
            string run = m.Value;
            foreach (string word in CellStopWords)
            {
                int at = run.IndexOf(word, StringComparison.Ordinal);
                if (at >= 0) run = run[..at];
            }
            if (run.Length is >= 2 and <= 4) return run;
        }
        return null;
    }

    private static readonly Regex CjkRun = new(@"[一-龥]+", RegexOptions.Compiled);

    /// <summary>Rotates the page clockwise in 90° steps — the ADF's way of presenting a
    /// landscape page on a portrait canvas.</summary>
    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        bool swap = degrees is 90 or 270;
        var target = new SKBitmap(
            swap ? source.Height : source.Width,
            swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(target);
        switch (degrees)
        {
            case 90: canvas.Translate(target.Width, 0); canvas.RotateDegrees(90); break;
            case 180: canvas.Translate(target.Width, target.Height); canvas.RotateDegrees(180); break;
            case 270: canvas.Translate(0, target.Height); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    private static SKBitmap Crop(SKBitmap source, int x, int y, int width, int height)
    {
        var target = new SKBitmap(width, height);
        using var canvas = new SKCanvas(target);
        canvas.DrawBitmap(
            source,
            new SKRect(x, y, x + width, y + height),
            new SKRect(0, 0, width, height));
        return target;
    }

    private static SKBitmap Scale(SKBitmap source, int factor)
    {
        var target = new SKBitmap(source.Width * factor, source.Height * factor);
        using var canvas = new SKCanvas(target);
        canvas.DrawBitmap(
            source,
            new SKRect(0, 0, source.Width, source.Height),
            new SKRect(0, 0, target.Width, target.Height));
        return target;
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
