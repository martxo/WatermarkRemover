using System.Text;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Recognition;

/// <summary>
/// Pulls field labels and their values out of full-page recognised lines — the structured
/// "who is the 经营者, where does the value sit" data the recognition report exists for.
/// Label matching reuses <see cref="LicenseTemplateStage.MatchAnchor"/>, so inserted spaces,
/// colons and truncated labels behave exactly like the template layout stage sees them;
/// a label split across lines never matches here and is left for the reviewer's anchor_split rule.
/// </summary>
public static class LicenseFieldExtractor
{
    /// <summary>Same label-row threshold as LicenseTemplateStage.BuildBands (side-by-side labels).</summary>
    private const double SameRowFactor = 0.6;

    /// <summary>Extracts one observation per label found, in reading order. Empty when no label matches.</summary>
    public static IReadOnlyList<FieldObservation> Extract(IReadOnlyList<RecognizedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        IReadOnlyList<RecognizedLine> ordered = InReadingOrder(lines);
        if (ordered.Count == 0) return Array.Empty<FieldObservation>();

        var isLabel = new bool[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
            isLabel[i] = LicenseTemplateStage.MatchAnchor(ordered[i].Text) is not null;

        var observations = new List<FieldObservation>();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!isLabel[i]) continue;

            RecognizedLine labelLine = ordered[i];
            RectD labelBox = labelLine.Bounds!.Value;

            // Value continuation: non-label lines after this label line, stopping at the next
            // label line (经营范围 wraps; 名称/经营者 values usually end on the line itself).
            var continuation = new List<RecognizedLine>();
            for (int j = i + 1; j < ordered.Count && !isLabel[j]; j++)
                continuation.Add(ordered[j]);

            List<(string Label, string Value)> entries = SplitValues(labelLine.Text);
            if (entries.Count == 0) continue;

            RectD lastBox = labelBox;
            foreach (RecognizedLine line in continuation)
                lastBox = Union(lastBox, line.Bounds!.Value);

            for (int e = 0; e < entries.Count; e++)
            {
                bool isLast = e == entries.Count - 1;
                string value = entries[e].Value;
                // Side-by-side labels share the line; only the trailing label owns what follows.
                if (isLast && continuation.Count > 0)
                    value += string.Concat(continuation.Select(c => c.Text.Trim()));

                observations.Add(new FieldObservation(entries[e].Label, labelBox, value,
                    isLast ? lastBox : labelBox));
            }
        }

        return observations;
    }

    /// <summary>
    /// Splits one label line into (label, same-line value) entries. The line is normalised the
    /// way MatchAnchor normalises (whitespace/colon stripped) with a map back to original
    /// offsets, so "名　称：厦门…" yields label 名称 with value "厦门…".
    /// </summary>
    private static List<(string Label, string Value)> SplitValues(string text)
    {
        string? lineLabel = LicenseTemplateStage.MatchAnchor(text);
        if (lineLabel is null) return new();

        // Normalized view plus a map: map[i] = original offset of normalized char i.
        var map = new int[text.Length + 1];
        var norm = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c is '：' or ':') continue;
            map[norm.Length] = i;
            norm.Append(c);
        }
        map[norm.Length] = text.Length;
        string n = norm.ToString();

        // Positional scan for every full anchor; longest-first labels win at the same spot.
        var matches = new List<(int Pos, int Len, string Label)>();
        foreach (string anchor in LicenseTemplateStage.FieldLabels)
        {
            int from = 0, at;
            while ((at = n.IndexOf(anchor, from, StringComparison.Ordinal)) >= 0)
            {
                matches.Add((at, anchor.Length, anchor));
                from = at + anchor.Length;
            }
        }

        if (matches.Count == 0)
        {
            // Truncated label (MatchAnchor's ≥4-glyph prefix rule) — label known, value empty.
            return new() { (lineLabel, string.Empty) };
        }

        var ordered = matches
            .GroupBy(m => m.Pos)
            .Select(g => g.OrderByDescending(m => m.Len).First())
            .OrderBy(m => m.Pos)
            .ToList();

        var result = new List<(string Label, string Value)>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            int valueStart = ordered[i].Pos + ordered[i].Len;
            int valueEnd = i + 1 < ordered.Count ? ordered[i + 1].Pos : n.Length;
            string value = valueEnd > valueStart ? text[map[valueStart]..map[valueEnd]] : string.Empty;
            result.Add((ordered[i].Label, CleanValue(value)));
        }

        // Defensive: the line-leading label (MatchAnchor) must stay the first entry even when it
        // only matched in truncated form and the scan found other labels further in.
        if (ordered[0].Pos != 0 || ordered[0].Label != lineLabel)
            result.Insert(0, (lineLabel, string.Empty));

        return result;
    }

    private static string CleanValue(string value) =>
        value.Trim().TrimStart('：', ':').Trim();

    /// <summary>
    /// Visual reading order: rows top-to-bottom (stable), side-by-side lines within a row
    /// left-to-right by x — the same 0.6×height rule LicenseTemplateStage.BuildBands uses for
    /// label rows. Engine output arrives in detection/region order, which is neither stable nor
    /// visual, so both field extraction and the report/reviewer depend on this reordering.
    /// Lines without usable bounds are dropped (no position = no reading order).
    /// </summary>
    public static IReadOnlyList<RecognizedLine> InReadingOrder(IReadOnlyList<RecognizedLine> lines)
    {
        var bounded = lines
            .Where(l => l.Bounds is { } b && b.Width > 0 && b.Height > 0)
            .OrderBy(l => l.Bounds!.Value.Y + l.Bounds.Value.Height / 2)
            .ToList();
        if (bounded.Count == 0) return bounded;

        var rows = new List<List<RecognizedLine>>();
        foreach (RecognizedLine line in bounded)
        {
            RectD b = line.Bounds!.Value;
            double cy = b.Y + b.Height / 2;

            int last = rows.Count - 1;
            if (last >= 0)
            {
                RectD pb = rows[last][^1].Bounds!.Value;
                double pcy = pb.Y + pb.Height / 2;
                if (Math.Abs(cy - pcy) <= SameRowFactor * Math.Max(b.Height, pb.Height))
                {
                    rows[last].Add(line);
                    continue;
                }
            }
            rows.Add(new List<RecognizedLine> { line });
        }

        return rows.SelectMany(r => r.OrderBy(l => l.Bounds!.Value.X)).ToList();
    }

    private static RectD Union(RectD a, RectD b)
    {
        double minX = Math.Min(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxX = Math.Max(a.X + a.Width, b.X + b.Width);
        double maxY = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }
}
