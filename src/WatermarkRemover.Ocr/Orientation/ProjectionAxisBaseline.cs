using SkiaSharp;

namespace WatermarkRemover.Ocr.Orientation;

/// <summary>
/// 零依赖几何基线（盲扫交叉校验，v1.16.83）：纯投影统计，不跑 OCR、不跑模型。
/// 横排文字页的行投影是「墨条-空隙」的高方差序列，列投影则平滑；竖排文字页相反。
/// 比较两个投影的变异系数即可判定文字轴（横排 vs 竖排），用于交叉校验整页分类器的
/// 0/180 vs 90/270 大类——它分不开 0 与 180（同为横排），那一步交给分类器。
/// 输入用 256px 探针图（与空白判定同源，一次编码两处复用）；低对比/无墨样本弃权。
/// </summary>
public static class ProjectionAxisBaseline
{
    /// <summary>行/列投影变异系数之比超过此值才敢下结论（表格页两个投影都起伏 → 弃权）。</summary>
    internal const double AxisRatioGate = 1.4;

    /// <summary>墨量下限：近乎全白的样本没有可测的投影结构。</summary>
    internal const double MinInkRatio = 0.005;

    public readonly record struct AxisVote(bool IsHorizontalText, double Confidence);

    /// <summary>
    /// Returns which axis the page's text runs along, or null when the projection structure is
    /// too weak or too ambiguous to cross-check the classifier with.
    /// </summary>
    public static AxisVote? Judge(byte[] probePng)
    {
        using SKBitmap? bitmap = SKBitmap.Decode(probePng);
        if (bitmap is null || bitmap.PeekPixels() is not { } pixmap) return null;

        ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan();
        int width = bitmap.Width, height = bitmap.Height, bpp = bitmap.BytesPerPixel;

        var rowInk = new int[height];
        var colInk = new int[width];
        long inked = 0, sampled = 0;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * pixmap.RowBytes;
            for (int x = 0; x < width; x++)
            {
                int p = rowStart + x * bpp;
                int gray = (pixels[p + 2] * 299 + pixels[p + 1] * 587 + pixels[p] * 114) / 1000;
                sampled++;
                if (gray < 200)
                {
                    inked++;
                    rowInk[y]++;
                    colInk[x]++;
                }
            }
        }

        if (sampled == 0 || inked / (double)sampled < MinInkRatio) return null;

        double rowCv = VariationCoefficient(rowInk);
        double colCv = VariationCoefficient(colInk);
        if (rowCv <= 0 || colCv <= 0) return null;

        double ratio = rowCv / colCv;
        bool horizontal = ratio >= 1;
        // 置信度：比值离 1 越远越可信，过 1.4 才够格当交叉校验。
        double confidence = Math.Min(1.0, Math.Abs(Math.Log(ratio)) / Math.Log(AxisRatioGate));
        if (confidence < 1.0) return null; // 比值未过门槛：弃权，不参与交叉校验

        return new AxisVote(horizontal, confidence);
    }

    /// <summary>标准差 / 均值：横排文字的行投影尖峰起伏大（行墨-行空交替），列投影平滑。</summary>
    private static double VariationCoefficient(int[] profile)
    {
        double mean = 0;
        for (int i = 0; i < profile.Length; i++) mean += profile[i];
        if (profile.Length == 0 || mean <= 0) return 0;
        mean /= profile.Length;

        double variance = 0;
        for (int i = 0; i < profile.Length; i++)
        {
            double d = profile[i] - mean;
            variance += d * d;
        }
        variance /= profile.Length;
        return Math.Sqrt(variance) / mean;
    }
}
