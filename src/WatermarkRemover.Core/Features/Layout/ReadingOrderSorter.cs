namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// Sorts detected regions into human reading order: rows top-to-bottom (two regions share a row
/// when their vertical centers sit within 60% of the row's median height), inside a row
/// left-to-right, and regions sharing the same column fall back to element kind — title before
/// body before table before figure. Pure and small so the pipeline can reuse it across pages.
/// </summary>
public static class ReadingOrderSorter
{
    /// <summary>Left edges within this distance (normalized) count as the same column.</summary>
    private const double SameColumnTolerance = 0.03;

    public static IReadOnlyList<LayoutRegion> Sort(IReadOnlyList<LayoutRegion> regions)
    {
        if (regions.Count <= 1) return regions;

        var byY = regions
            .OrderBy(r => CenterY(r))
            .ThenBy(r => r.Bounds.X)
            .ToList();

        var rows = new List<List<LayoutRegion>>();
        foreach (LayoutRegion region in byY)
        {
            List<LayoutRegion>? current = rows.Count > 0 ? rows[^1] : null;
            if (current is not null)
            {
                double rowCenter = Median(current, CenterY);
                double rowHeight = Median(current, r => r.Bounds.Height);
                if (Math.Abs(CenterY(region) - rowCenter) <= 0.6 * rowHeight)
                {
                    current.Add(region);
                    continue;
                }
            }

            rows.Add(new List<LayoutRegion> { region });
        }

        var ordered = new List<LayoutRegion>(regions.Count);
        foreach (List<LayoutRegion> row in rows)
            ordered.AddRange(SortRow(row));

        return ordered;
    }

    /// <summary>Left-to-right inside a row; regions sharing a column break by kind, then by y.</summary>
    private static IEnumerable<LayoutRegion> SortRow(List<LayoutRegion> row)
    {
        List<LayoutRegion> byX = row.OrderBy(r => r.Bounds.X).ToList();

        var result = new List<LayoutRegion>(byX.Count);
        int i = 0;
        while (i < byX.Count)
        {
            int j = i;
            while (j + 1 < byX.Count && byX[j + 1].Bounds.X - byX[j].Bounds.X <= SameColumnTolerance)
                j++;

            // Cluster i..j shares one column: title before body before table before figure.
            result.AddRange(byX
                .Skip(i)
                .Take(j - i + 1)
                .OrderBy(r => KindRank(r.Kind))
                .ThenBy(CenterY));
            i = j + 1;
        }

        return result;
    }

    private static double CenterY(LayoutRegion r) => r.Bounds.Y + r.Bounds.Height / 2;

    private static double Median(List<LayoutRegion> regions, Func<LayoutRegion, double> selector)
    {
        double[] values = regions.Select(selector).OrderBy(v => v).ToArray();
        int mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>标题 0 → 正文 1 → 表格 2 → 图 3; unknown kinds count as body text.</summary>
    private static int KindRank(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return 1;
        if (kind.Contains("title", StringComparison.OrdinalIgnoreCase) || kind.Contains("标题")) return 0;
        if (kind.Contains("table", StringComparison.OrdinalIgnoreCase) || kind.Contains("表格")) return 2;
        if (kind.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("figure", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("chart", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("图")) return 3;
        return 1;
    }
}
