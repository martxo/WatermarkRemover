using System.Text.RegularExpressions;

namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Extracts the customer's name next to the profile's name labels. OCR output is imperfect:
/// labels get truncated (经营者 → 者), split across lines, or run together without spaces, so
/// candidates come from several shapes and are cleaned with field-label word boundaries before
/// validation via <see cref="SurnameTable"/>.
/// </summary>
public static class NameExtractor
{
    /// <summary>A name is 2-4 CJK ideographs.</summary>
    private static readonly Regex NameShape = new(@"[一-龥]{2,4}", RegexOptions.Compiled);

    /// <summary>
    /// Field-label words a name must never contain at its edges. OCR runs text together
    /// ("者林孝明经营范围"), so a captured run can swallow the start of the next field label;
    /// these words mark where the name actually ends (and what to strip from its start).
    /// </summary>
    private static readonly string[] FieldWords =
    {
        "姓名", "性别", "民族", "出生", "住址", "身份", "签发", "有效", "经营", "法定",
        "投资", "执行", "负责", "组成", "登记", "注册", "成立", "资本", "机关", "期限",
        "日期", "电话", "地址", "户籍", "文化", "职业", "婚姻", "户口", "常住", "统一",
        "社会", "信用", "代码", "范围", "场所", "方式", "类型", "形式", "企业", "个体",
    };

    /// <summary>Extracts the best name candidate for the profile's labels, or null.</summary>
    public static string? Extract(IReadOnlyList<string> lines, DocumentProfile profile)
    {
        string text = string.Join("\n", lines);

        string? best = null;
        int bestScore = -1;

        void Consider(string candidate)
        {
            // A weak candidate (no common surname, not even a rarer 4-char name) is usually
            // OCR noise that survived cleaning — leave those out entirely.
            int score = SurnameTable.ScoreCandidate(candidate);
            if (score < 0) return;
            if (score == 0 && candidate.Length < 4) return;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        // Cleans one captured run: strips a leading field word ("姓名张三") and trims a
        // swallowed trailing label ("林孝明经" + 营范围 → 林孝明). The run's offsets are
        // relative to its source (the joined text or a single line).
        void ConsiderRun(string source, int start, int end)
        {
            bool stripped;
            do
            {
                stripped = false;
                foreach (string word in FieldWords)
                {
                    if (MatchesAt(source, start, word))
                    {
                        start += word.Length;
                        stripped = true;
                        break;
                    }
                }
            } while (stripped && end - start > 4);

            for (int p = start + 2; p < end; p++)
            {
                foreach (string word in FieldWords)
                {
                    if (MatchesAt(source, p, word))
                    {
                        end = p;
                        break;
                    }
                }
                if (end == p) break;
            }

            if (end - start is >= 2 and <= 4)
                Consider(source[start..end]);
        }

        foreach (string label in profile.NameLabels)
        {
            // Shape 1: the full label, then the name (optionally after a 姓名 filler) on the
            // same line. \s* spans newlines so "法定代表人" + next-line name also lands here.
            foreach (Match m in Regex.Matches(
                         text, Regex.Escape(label) + @"\s*[:：]?\s*(?:姓名)?\s*([一-龥]{2,4})"))
                ConsiderRun(text, m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length);

            // Shape 2: OCR-truncated label: progressively shorter tails (经营者 → 营者 → 者).
            for (int len = label.Length - 1; len >= 1; len--)
            {
                string tail = label[^len..];
                // Skip generic single chars that would match anywhere ("人", "名").
                if (len == 1 && (tail == "人" || tail == "名")) continue;

                foreach (Match m in Regex.Matches(
                             text, Regex.Escape(tail) + @"\s*[:：]?\s*([一-龥]{2,4})"))
                    ConsiderRun(text, m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length);
            }

            // Shape 3: the label alone on its line, the name on the next non-empty line.
            for (int i = 0; i < lines.Count; i++)
            {
                int at = lines[i].IndexOf(label, StringComparison.Ordinal);
                if (at < 0) continue;

                string rest = lines[i][(at + label.Length)..].Trim().Trim('：', ':');
                if (rest.Length > 0) continue; // shape 1 already covers a same-line name

                for (int j = i + 1; j < lines.Count; j++)
                {
                    string next = lines[j].Trim();
                    if (next.Length == 0) continue;
                    if (NameShape.Match(next) is { Success: true } m)
                        ConsiderRun(next, m.Index, m.Index + m.Length);
                    break;
                }
            }
        }

        return bestScore >= 0 ? best : null;
    }

    private static bool MatchesAt(string text, int index, string word) =>
        index + word.Length <= text.Length &&
        string.CompareOrdinal(text, index, word, 0, word.Length) == 0;
}
