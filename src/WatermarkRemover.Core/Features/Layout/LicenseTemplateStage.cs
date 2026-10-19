using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// Zero-model layout block for fixed-format certificates (business licences in particular).
/// It runs a full-image pass through the injected <see cref="IRegionProbe"/>, looks for known
/// field labels (统一社会信用代码 / 名称 / 住所 / 法定代表人 … across company, sole
/// proprietorship, partnership and individual-business variants), and cuts one horizontal band
/// per label row — from the label to the right edge, ending where the next label row starts —
/// so wrapped values (e.g. 经营范围) stay with their label and the pipeline re-reads each field
/// in document order. Anchors found without bounds (engine has no positioning) or no anchor at
/// all both degrade to a single full-image region, i.e. plain direct recognition.
/// </summary>
public sealed class LicenseTemplateStage : ILayoutStage
{
    public static readonly LicenseTemplateStage Instance = new();

    /// <summary>
    /// Field labels per subject type, longest first so the longest match wins:
    /// company/non-company enterprise → 法定代表人, sole proprietorship → 投资人,
    /// partnership → 执行事务合伙人/负责人, individual business → 经营者.
    /// </summary>
    private static readonly string[] Anchors =
    {
        "统一社会信用代码", "执行事务合伙人", "法定代表人", "认证编号",
        "注册资本", "实缴出资额", "认缴出资额", "成立日期", "成立时间",
        "营业期限", "经营范围", "登记机关", "核准日期", "经营者",
        "投资人", "合伙人", "负责人", "名称", "类型", "住所", "住址",
    };

    /// <summary>Matching order: longest label first, so the longest match always wins.</summary>
    private static readonly string[] AnchorsLongestFirst =
        Anchors.OrderByDescending(a => a.Length).ToArray();

    /// <summary>All field labels, longest first — shared with field extraction in the report.</summary>
    public static IReadOnlyList<string> FieldLabels => AnchorsLongestFirst;

    public string Name => "营业执照模板定位";

    public async Task<IReadOnlyList<LayoutRegion>> DetectRegionsAsync(byte[] imageBytes, IRegionProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        IReadOnlyList<RecognizedLine> lines = await probe.RecognizeAsync(imageBytes);

        var anchors = new List<RecognizedLine>();
        foreach (RecognizedLine line in lines)
        {
            if (line.Bounds is not { } bounds || MatchAnchor(line.Text) is null)
                continue;
            if (bounds.Width > 0 && bounds.Height > 0)
                anchors.Add(line);
        }

        if (anchors.Count == 0)
            return new[] { new LayoutRegion(new RectD(0, 0, 1, 1), "page") };

        return BuildBands(anchors);
    }

    /// <summary>
    /// Returns the matched label when the line starts with a known field label — tolerating
    /// OCR noise such as inserted spaces ("名　称"), colons and truncated labels
    /// ("统一社会信用代" missing its last glyph). Longest label wins so 名称 cannot shadow
    /// a longer compound label.
    /// </summary>
    public static string? MatchAnchor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string norm = new(text.Where(c => !char.IsWhiteSpace(c) && c is not '：' and not ':').ToArray());
        if (norm.Length == 0) return null;

        foreach (string anchor in AnchorsLongestFirst)
        {
            if (norm.StartsWith(anchor, StringComparison.Ordinal))
                return anchor;
            // Truncated label: the whole line is a near-complete label (≥4 glyphs, so short
            // labels like 名称 can only match in full).
            if (norm.Length >= 4 && norm.Length < anchor.Length && anchor.StartsWith(norm, StringComparison.Ordinal))
                return anchor;
        }

        return null;
    }

    /// <summary>
    /// One band per label row: top = first label's top edge, bottom = next row's top edge
    /// (page bottom for the last row), left = leftmost label. Bands are contiguous and never
    /// overlap, so the pipeline's per-crop recognition cannot duplicate a line.
    /// </summary>
    private static List<LayoutRegion> BuildBands(IReadOnlyList<RecognizedLine> anchors)
    {
        var sorted = anchors
            .OrderBy(a => a.Bounds!.Value.Y + a.Bounds.Value.Height / 2)
            .ToList();

        var bands = new List<(double Top, double Bottom, double Left, double CenterY, double Height)>();
        foreach (RecognizedLine anchor in sorted)
        {
            RectD b = anchor.Bounds!.Value;
            double centerY = b.Y + b.Height / 2;

            int last = bands.Count - 1;
            if (last >= 0 && Math.Abs(centerY - bands[last].CenterY) <= 0.6 * Math.Max(b.Height, bands[last].Height))
            {
                // Same label row (two labels side by side) — merge into one band.
                var band = bands[last];
                bands[last] = (Math.Min(band.Top, b.Y), Math.Max(band.Bottom, b.Y + b.Height),
                    Math.Min(band.Left, b.X), centerY, b.Height);
            }
            else
            {
                bands.Add((b.Y, b.Y + b.Height, b.X, centerY, b.Height));
            }
        }

        var regions = new List<LayoutRegion>(bands.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            double bottom = i + 1 < bands.Count ? bands[i + 1].Top : 1.0;
            double top = bands[i].Top;
            double left = bands[i].Left;
            if (bottom - top < 0.001 || 1 - left < 0.001) continue;
            regions.Add(new LayoutRegion(new RectD(left, top, 1 - left, bottom - top), "field"));
        }

        return regions;
    }
}
