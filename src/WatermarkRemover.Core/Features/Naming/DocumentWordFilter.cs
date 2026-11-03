namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Keeps document-title words out of names and file stems. OCR regularly glues a title onto
/// the value it reads — 「征信庞结」/「庞结征信」 where only 庞结 is the customer — and the
/// credit report's type short name (征信) used to be concatenated onto every stem as well,
/// so scans landed as 庞结征信.pdf. Cleaning is deliberately conservative: only KNOWN
/// document words, only at the edges, and only while a plausible name remains. Characters
/// inside a candidate are never touched (庞信征 / 李报恩 survive verbatim), and a candidate
/// that is nothing but document words (征信) is a title, not a name.
/// </summary>
public static class DocumentWordFilter
{
    /// <summary>
    /// Known document-title words, longest first so compound titles strip as one unit
    /// (个人征信 before 个人, 企业征信 before 征信). Deliberately excluded: 信用 alone —
    /// a 信用合作社 is a company name, not a title fragment.
    /// </summary>
    private static readonly string[] Words =
    {
        "个人信用报告", "个人征信", "征信报告", "信用报告", "企业征信", "信用卡",
        "征信", "明细", "查询", "报告", "个人",
    };

    /// <summary>
    /// Trims document words off both edges (looping until stable) and returns what remains,
    /// or null when nothing name-like is left: the candidate was all document words, or the
    /// trim would leave fewer than two characters (王明细 → 王 is not a name).
    /// </summary>
    public static string? StripName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        string s = candidate.Trim();
        bool stripped;
        do
        {
            stripped = false;
            foreach (string word in Words)
            {
                if (s.StartsWith(word, StringComparison.Ordinal))
                {
                    s = s[word.Length..];
                    stripped = true;
                    break;
                }
                if (s.EndsWith(word, StringComparison.Ordinal))
                {
                    s = s[..^word.Length];
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        return s.Length < 2 ? null : s;
    }
}
