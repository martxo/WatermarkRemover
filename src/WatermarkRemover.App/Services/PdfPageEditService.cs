using WatermarkRemover.Pdf;
using WatermarkRemover.Pdf.Preview;

namespace WatermarkRemover.App.Services;

/// <summary>
/// All in-place PDF page mutations in one place: rotate, delete, reorder, insert. Each runs under
/// the render cache's single-writer gate, remembers the new file stamp and drops the stale renders,
/// so callers only deal with the UI afterwards.
/// </summary>
public sealed class PdfPageEditService
{
    private readonly PdfPageRenderCache _cache;
    private readonly PdfDocumentFile _file;

    public PdfPageEditService(PdfPageRenderCache cache, PdfDocumentFile file)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <summary>
    /// Turns one page clockwise and drops only that page's rendered bytes.
    /// Returns true when a locked original forced the result into a "原名 (已旋转).pdf" copy.
    /// </summary>
    public async Task<bool> RotateAsync(int pageIndex)
    {
        // Read the target inside the gate: a rename the user made behind our back is resolved
        // there, and only a path *different from the one written* means a locked-copy fallback.
        string original = _file.Path;
        string path = await _cache.RunWriteAsync(() =>
        {
            original = _file.Path;
            return PdfPageRotator.RotateClockwise(original, pageIndex);
        }).ConfigureAwait(false);
        bool savedAsCopy = Follow(path, original);
        _cache.InvalidatePage(pageIndex);
        return savedAsCopy;
    }

    /// <summary>
    /// Removes one page; every index after it shifts, so the whole cache goes.
    /// Returns true when a locked original forced the result into a copy beside it.
    /// </summary>
    public async Task<bool> DeleteAsync(int pageIndex)
    {
        string original = _file.Path;
        string path = await _cache.RunWriteAsync(() =>
        {
            original = _file.Path;
            return PdfPageRotator.DeletePage(original, pageIndex);
        }).ConfigureAwait(false);
        bool savedAsCopy = Follow(path, original);
        _cache.InvalidateAll();
        return savedAsCopy;
    }

    /// <summary>
    /// Moves a page to its new slot in one save; every cache key is index-based, so all go.
    /// Returns true when a locked original forced the result into a copy beside it.
    /// </summary>
    public async Task<bool> MoveAsync(int fromIndex, int toIndex)
    {
        string original = _file.Path;
        string? path = await _cache.RunWriteAsync(() =>
        {
            original = _file.Path;
            return PdfPageOrderer.MovePage(original, fromIndex, toIndex);
        }).ConfigureAwait(false);
        bool savedAsCopy = path is not null && Follow(path, original);
        _cache.InvalidateAll();
        return savedAsCopy;
    }

    /// <summary>
    /// A locked original (WPS, another viewer) makes the edit land beside it as a copy instead of
    /// failing; the session then follows that copy so every later tile, render and edit uses it.
    /// </summary>
    private bool Follow(string newPath, string original)
    {
        if (!string.Equals(newPath, original, StringComparison.OrdinalIgnoreCase))
        {
            _file.RelocateTo(newPath);
            _file.RememberStamp(force: true);
            return true;
        }

        _file.RememberStamp(force: true);
        return false;
    }

    /// <summary>
    /// Splices every page of <paramref name="scannedPdfPath"/> into the document. A locked original
    /// is saved beside it as a copy and the session follows that path; the caller owns the
    /// post-insert UI rebuild.
    /// </summary>
    public async Task<PdfInsertResult> InsertAsync(int insertAfterIndex, string scannedPdfPath)
    {
        PdfInsertResult result = await _cache
            .RunWriteAsync(() => PdfPageInserter.InsertAfterDetailed(_file.Path, insertAfterIndex, scannedPdfPath))
            .ConfigureAwait(false);
        if (result.SavedAsCopy) _file.RelocateTo(result.PdfPath);
        return result;
    }

    /// <summary>Turns the whole document in one save (auto-orientation).</summary>
    public async Task RotateAllAsync(int steps)
    {
        await _cache.RunWriteAsync(() => PdfPageRotator.RotateAll(_file.Path, steps)).ConfigureAwait(false);
        _file.RememberStamp(force: true);
        _cache.InvalidateAll();
    }

    /// <summary>Deletes the given pages in one save; returns how many actually went.</summary>
    public async Task<int> DeletePagesAsync(IReadOnlyList<int> pageIndices)
    {
        int removed = 0;
        await _cache.RunWriteAsync(() => removed = PdfPageRotator.DeletePages(_file.Path, pageIndices)).ConfigureAwait(false);
        if (removed > 0)
        {
            _file.RememberStamp(force: true);
            _cache.InvalidateAll();
        }
        return removed;
    }

    /// <summary>Turns several pages, each by its own quarter-turn count, in one save.</summary>
    public async Task RotatePagesAsync(IReadOnlyList<(int PageIndex, int QuarterTurns)> turns)
    {
        await _cache.RunWriteAsync(() => PdfPageRotator.RotatePages(_file.Path, turns)).ConfigureAwait(false);
        _file.RememberStamp(force: true);
        foreach (var (pageIndex, _) in turns)
            _cache.InvalidatePage(pageIndex);
    }
}
