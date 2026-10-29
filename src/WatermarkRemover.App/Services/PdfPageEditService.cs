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

    /// <summary>Turns one page clockwise and drops only that page's rendered bytes.</summary>
    public async Task RotateAsync(int pageIndex)
    {
        await _cache.RunWriteAsync(() => PdfPageRotator.RotateClockwise(_file.Path, pageIndex)).ConfigureAwait(false);
        _file.RememberStamp(force: true);
        _cache.InvalidatePage(pageIndex);
    }

    /// <summary>Removes one page; every index after it shifts, so the whole cache goes.</summary>
    public async Task DeleteAsync(int pageIndex)
    {
        await _cache.RunWriteAsync(() => PdfPageRotator.DeletePage(_file.Path, pageIndex)).ConfigureAwait(false);
        _file.RememberStamp(force: true);
        _cache.InvalidateAll();
    }

    /// <summary>Moves a page to its new slot in one save; every cache key is index-based, so all go.</summary>
    public async Task MoveAsync(int fromIndex, int toIndex)
    {
        await _cache.RunWriteAsync(() => PdfPageOrderer.MovePage(_file.Path, fromIndex, toIndex)).ConfigureAwait(false);
        _file.RememberStamp(force: true);
        _cache.InvalidateAll();
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
