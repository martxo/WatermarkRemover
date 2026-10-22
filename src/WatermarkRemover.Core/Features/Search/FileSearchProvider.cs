namespace WatermarkRemover.Core.Features.Search;

/// <summary>One file or folder hit returned by a name index. Size and date are best-effort.</summary>
public sealed record FileSearchResult(
    string FullPath,
    bool IsFolder,
    long Size,
    DateTime? ModifiedAt)
{
    public string Name => Path.GetFileName(
        FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>
/// A name index the 快速搜索 page can query (Everything today). Implementations must never throw
/// for an absent or stopped client: report <see cref="IsAvailable"/> false and return no results,
/// so the page degrades to an install hint instead of an error dialog.
/// </summary>
public interface IFileSearchProvider
{
    /// <summary>Name shown in the page hint, e.g. "Everything".</summary>
    string DisplayName { get; }

    /// <summary>True when the backing client is installed AND running.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs one query. The text is passed through as-is (the index's own syntax applies), so
    /// callers stay free to use wildcards, ext: filters, etc.
    /// </summary>
    Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);
}
