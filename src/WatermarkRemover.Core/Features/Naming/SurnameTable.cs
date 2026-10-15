namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Fast name validation: common Chinese surnames plus basic shape rules. This is a heuristic
/// filter for OCR output, not a legal-name registry — its job is to reject OCR noise
/// (numbers, symbols, random junk) and prefer realistic names.
/// </summary>
public static class SurnameTable
{
    /// <summary>The most common Chinese surnames (roughly the top few hundred).</summary>
    private const string CommonSurnames =
        "王李张刘陈杨黄赵吴周徐孙马朱胡郭何高林罗郑梁谢宋唐许韩冯邓曹彭曾肖田董袁潘于蒋蔡余杜叶程苏魏吕丁任沈姚卢姜崔钟谭陆汪范金石廖贾夏韦付方白邹孟熊秦邱江尹薛闫段雷侯龙史陶黎贺顾毛郝龚邵万钱严覃武戴莫孔向汤";

    private static readonly HashSet<char> Surnames = new(CommonSurnames);

    public static bool IsCommonSurname(char c) => Surnames.Contains(c);

    /// <summary>
    /// Scores a captured name candidate. Returns -1 when the candidate is clearly invalid
    /// (wrong shape or OCR noise); higher is better.
    /// </summary>
    public static int ScoreCandidate(string candidate)
    {
        if (candidate.Length is < 2 or > 4) return -1;

        foreach (char c in candidate)
        {
            // Valid name characters are CJK ideographs; anything else is OCR noise.
            if (c < 0x4E00 || c > 0x9FFF) return -1;
        }

        int score = 0;
        if (IsCommonSurname(candidate[0])) score += 2;

        // Three-character names are by far the most common in practice.
        if (candidate.Length == 3) score += 1;

        return score;
    }
}
