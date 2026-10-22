using WatermarkRemover.Index.Indexing;

namespace WatermarkRemover.Tests.Index;

/// <summary>Query semantics mirror Everything's defaults: name-only, case-insensitive substring.</summary>
public class IndexQueryTests
{
    private const uint Directory = 0x10;
    private const uint Archive = 0x20;

    private static FileIndexStore NewStore()
    {
        var store = new FileIndexStore(@"C:\");
        store.Upsert(new IndexEntry(10, FileIndexStore.RootFileId, Directory, "alpha-dir"));
        store.Upsert(new IndexEntry(20, FileIndexStore.RootFileId, Archive, "alpha.pdf"));
        store.Upsert(new IndexEntry(21, FileIndexStore.RootFileId, Archive, "alpha.txt"));
        store.Upsert(new IndexEntry(22, FileIndexStore.RootFileId, Archive, "my-alpha-doc.txt"));
        store.Upsert(new IndexEntry(23, FileIndexStore.RootFileId, Archive, "beta.txt"));
        return store;
    }

    private static IReadOnlyList<IndexMatch> Run(FileIndexStore store, string query, int max = 50) =>
        IndexQuery.Search(store, SearchPattern.Parse(query)!, max);

    [Fact]
    public void Blank_query_parses_to_null()
    {
        Assert.Null(SearchPattern.Parse(null));
        Assert.Null(SearchPattern.Parse("   "));
    }

    [Fact]
    public void Substring_search_is_case_insensitive()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "ALPHA");

        Assert.Equal(4, hits.Count);
        Assert.DoesNotContain(hits, h => h.Entry.Name == "beta.txt");
    }

    [Fact]
    public void Exact_and_prefix_matches_rank_above_word_boundary_and_substring()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "alpha");

        Assert.Equal(new[] { 1, 1, 1, 2 }, hits.Select(h => h.Score));
        Assert.Equal("my-alpha-doc.txt", hits[^1].Entry.Name);
    }

    [Fact]
    public void Exact_name_wins_over_prefix()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "alpha.txt");

        Assert.Equal(0, hits[0].Score);
        Assert.Equal("alpha.txt", hits[0].Entry.Name);
    }

    [Fact]
    public void Multiple_terms_are_anded()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "alpha doc");

        Assert.Single(hits);
        Assert.Equal("my-alpha-doc.txt", hits[0].Entry.Name);
    }

    [Fact]
    public void Terms_must_all_match_the_name()
    {
        Assert.Empty(Run(NewStore(), "alpha beta"));
    }

    [Fact]
    public void Wildcards_match_the_whole_name()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "*.pdf");

        Assert.Single(hits);
        Assert.Equal("alpha.pdf", hits[0].Entry.Name);
    }

    [Fact]
    public void Question_mark_matches_one_character()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "beta.tx?");

        Assert.Single(hits);
        Assert.Equal("beta.txt", hits[0].Entry.Name);
    }

    [Fact]
    public void Orphans_are_not_returned()
    {
        var store = NewStore();
        // A file whose parent was deleted: indexing it would surface a path that does not exist.
        store.Upsert(new IndexEntry(30, 777, Archive, "alpha-orphan.txt"));

        IReadOnlyList<IndexMatch> hits = Run(store, "alpha-orphan");

        Assert.Empty(hits);
    }

    [Fact]
    public void Results_are_capped_by_maxResults()
    {
        IReadOnlyList<IndexMatch> hits = Run(NewStore(), "alpha", max: 2);

        Assert.Equal(2, hits.Count);
    }

    [Theory]
    [InlineData("readme.md", "readme.md", true)]
    [InlineData("readme.md", "*.md", true)]
    [InlineData("readme.md", "*.txt", false)]
    [InlineData("readme.md", "read*", true)]
    [InlineData("readme.md", "*me.*", true)]
    [InlineData("readme.md", "?eadme.md", true)]
    [InlineData("readme.md", "readme.md?", false)]
    [InlineData("readme.md", "*.m*z", false)]
    [InlineData("readme.md", "*e*m*e*", true)]
    public void Wildcard_match_matches_star_and_question_semantics(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, SearchPattern.WildcardMatch(text, pattern));
    }
}
