using System.Text.RegularExpressions;

namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Extracts the customer's name next to the profile's name labels. OCR output is imperfect:
/// labels get truncated (经营者 → 者), split across lines, or run together without spaces, so
/// candidates come from several shapes and are cleaned with field-label word boundaries.
/// Validation is deliberately light (CJK shape + field-word rejection): names come from
/// label-anchored positions, not from surname dictionaries.
/// </summary>
public static class NameExtractor
{
    /// <summary>A name is 2-4 CJK ideographs.</summary>
    private static readonly Regex NameShape = new(@"[一-龥]{2,4}", RegexOptions.Compiled);

    /// <summary>A whole line that is nothing but a 2-4 char CJK name — the standalone-row
    /// fallback anchor for shredded layouts (see <see cref="Extract"/>).</summary>
    private static readonly Regex StandaloneNameLine = new(@"^[一-龥]{2,4}$", RegexOptions.Compiled);

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
        // "被查询者" sits before "查询" so the longer word strips first (the other order
        // would leave a stuck 被者 residue in IsNothingButFieldWords).
        "证件", "号码", "被查询者", "查询", "机构", "原因", "编号", "报告",
    };

    /// <summary>Extracts the best name candidate for the profile's labels, or null.</summary>
    public static string? Extract(IReadOnlyList<string> lines, DocumentProfile profile)
    {
        string text = string.Join("\n", lines);

        string? best = null;

        void Consider(string candidate)
        {
            if (best is not null) return; // first confident candidate wins

            // A candidate that still contains a field word ("被查询者", "本人查询") is a
            // label fragment, not a name — reject it whatever its shape.
            if (FieldWords.Any(w => candidate.Contains(w, StringComparison.Ordinal))) return;

            // A name is CJK ideographs only; anything else is OCR noise that survived cleaning.
            if (candidate.Any(c => c is < (char)0x4E00 or > (char)0x9FFF)) return;

            best = candidate;
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

            // Shape 3: the label alone on its line — or on a header row where the rest of the
            // line is nothing but other field labels (征信报告: 被查询者姓名 证件类型 证件号码
            // 查询机构 查询原因) — with the name as the first word of the value row below.
            for (int i = 0; i < lines.Count; i++)
            {
                int at = lines[i].IndexOf(label, StringComparison.Ordinal);
                if (at < 0) continue;

                string rest = lines[i][(at + label.Length)..].Trim().Trim('：', ':');
                if (rest.Length > 0 && !IsNothingButFieldWords(rest)) continue; // a same-line name is shape 1

                // WeChat OCR often shreds the header row into per-cell lines (被查询者姓名 /
                // 被查询者证件类型 / … / 查询原因) before the value row; hop over those cell
                // lines (bounded) so the name in the value row below is still reached.
                int headerCells = 0;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string next = lines[j].Trim();
                    if (next.Length == 0) continue;
                    if (IsNothingButFieldWords(next) && ++headerCells <= 4) continue;
                    if (NameShape.Match(next) is { Success: true } m)
                        ConsiderRun(next, m.Index, m.Index + m.Length);
                    break;
                }
            }
        }

        // Shape 4 (last resort): a rotated landscape licence comes back shredded —「经营者」
        // splits into one-char rows (营 / 者) and the name lands as its own row elsewhere, so
        // no label association survives the shapes above. On an already-classified document a
        // standalone 2-4 char CJK row is very likely the name; field words keep out the
        // remaining labels (登记机关), profile anchors keep out document titles (营业执照)
        // and calendar characters keep out page furniture (月日).
        if (best is null)
        {
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (!StandaloneNameLine.IsMatch(trimmed)) continue;
                if (FieldWords.Any(w => trimmed.Contains(w, StringComparison.Ordinal))) continue;
                if (profile.Anchors.Any(a => a.Regex.IsMatch(trimmed))) continue;
                if (trimmed.All(PageFurnitureChars.Contains)) continue;
                Consider(trimmed);
            }
        }

        return best;
    }

    /// <summary>Calendar/page-number characters a person's name is never made of.</summary>
    private static readonly char[] PageFurnitureChars = "年月日共页号第时".ToCharArray();

    private static bool MatchesAt(string text, int index, string word) =>
        index + word.Length <= text.Length &&
        string.CompareOrdinal(text, index, word, 0, word.Length) == 0;

    /// <summary>True when the text is built solely from field-label words and separators — the
    /// signature of a table header row, whose value row below carries the actual name.</summary>
    private static bool IsNothingButFieldWords(string text)
    {
        string remaining = text;
        bool stripped = true;
        while (stripped && remaining.Length > 0)
        {
            stripped = false;
            foreach (string word in FieldWords)
            {
                int at = remaining.IndexOf(word, StringComparison.Ordinal);
                if (at >= 0)
                {
                    remaining = remaining.Remove(at, word.Length);
                    stripped = true;
                }
            }
            remaining = remaining.Replace(" ", "").Replace("　", "").Trim('：', ':', '|', '\t');
        }
        return remaining.Length == 0;
    }
}
