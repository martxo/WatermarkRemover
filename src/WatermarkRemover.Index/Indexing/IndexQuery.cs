namespace WatermarkRemover.Index.Indexing;

/// <summary>A parsed query. Everything's defaults: name-only, case-insensitive substring.</summary>
public sealed class SearchPattern
{
    private readonly string[] _terms;
    private readonly string? _wildcard;

    private SearchPattern(string[] terms, string? wildcard)
    {
        _terms = terms;
        _wildcard = wildcard;
    }

    public static SearchPattern? Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        string trimmed = query.Trim();
        if (trimmed.Contains('*') || trimmed.Contains('?'))
            return new SearchPattern(Array.Empty<string>(), trimmed);

        string[] terms = trimmed.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 ? null : new SearchPattern(terms, null);
    }

    /// <summary>True when the name matches; score 0..3 is a coarse rank (lower is better).</summary>
    public bool TryMatch(string name, out int score)
    {
        score = int.MaxValue;

        if (_wildcard is not null)
        {
            if (!WildcardMatch(name, _wildcard)) return false;
            score = _wildcard.IndexOfAny(new[] { '*', '?' }) == 0 ? 2 : 1;
            return true;
        }

        int total = 0;
        foreach (string term in _terms)
        {
            int termScore = ScoreTerm(name, term);
            if (termScore < 0) return false;
            total += termScore;
        }

        score = total;
        return true;
    }

    private static int ScoreTerm(string name, string term)
    {
        if (name.Equals(term, StringComparison.OrdinalIgnoreCase)) return 0;

        if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 1;

        int index = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return -1;
        if (index > 0 && IsWordBoundary(name[index - 1])) return 2;
        return 3;
    }

    private static bool IsWordBoundary(char c) =>
        c is ' ' or '-' or '_' or '.' or '(' or ')' or '[' or ']' or ',' or '&' or '+' or '~';

    /// <summary>Linear wildcard match supporting <c>*</c> and <c>?</c> (no regex backtracking).</summary>
    public static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0;
        int star = -1, mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

/// <summary>A resolved hit: the index entry, its full path and its match score.</summary>
public readonly record struct IndexMatch(IndexEntry Entry, string FullPath, int Score);

public static class IndexQuery
{
    private const int SubstringCandidateLimit = 20_000;

    /// <summary>
    /// Scans the store and returns at most <paramref name="maxResults"/> best hits.
    /// Exact/prefix/boundary matches are always kept; substring-only matches are capped so a
    /// one-letter query cannot balloon into a million-entry sort.
    /// </summary>
    public static IReadOnlyList<IndexMatch> Search(FileIndexStore store, SearchPattern pattern, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pattern);
        if (maxResults <= 0) return Array.Empty<IndexMatch>();

        var strong = new List<IndexMatch>();
        var weak = new List<IndexMatch>();

        foreach (IndexEntry entry in store.Entries)
        {
            if (!pattern.TryMatch(entry.Name, out int score)) continue;

            string? path = store.ResolvePath(entry);
            if (path is null) continue; // orphan: parent deleted, not a real file any more

            var match = new IndexMatch(entry, path, score);
            if (score <= 2)
            {
                strong.Add(match);
            }
            else if (weak.Count < SubstringCandidateLimit)
            {
                weak.Add(match);
            }
        }

        IEnumerable<IndexMatch> ordered = strong
            .Concat(weak)
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Entry.Name.Length)
            .ThenBy(m => m.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase);

        return ordered.Take(maxResults).ToList();
    }
}
