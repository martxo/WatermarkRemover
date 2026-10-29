using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.Pdf.Preview;

/// <summary>
/// Per-document rendered-page cache plus the single writer gate. Rendered pages are immutable PNG
/// bytes keyed by (page, dpi); the UI decodes only the page it shows, and a miss costs one re-render.
///
/// The same <see cref="SemaphoreSlim"/> serialises in-place writes: a tile fill can be running while
/// a rotate/delete swaps the file, which would make pdfium read a half-replaced document.
/// </summary>
public sealed class PdfPageRenderCache
{
    /// <summary>Upper bound for one document's byte cache. Full-page PNGs are multi-megabyte LOH
    /// objects and the cache lives as long as the row does; over budget it is dropped wholesale —
    /// a miss only costs a re-render of the page actually being shown.</summary>
    private const long RenderCacheBudgetBytes = 48L * 1024 * 1024;

    private readonly IPdfPageRenderer _renderer;
    private readonly PdfDocumentFile _file;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<string>? _debug;
    private readonly Action<string>? _warn;

    private readonly ConcurrentDictionary<(int Page, int Dpi), byte[]> _renderedPages = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _bytes;
    private int _epoch;

    public PdfPageRenderCache(
        IPdfPageRenderer renderer,
        PdfDocumentFile file,
        CancellationToken cancellationToken = default,
        Action<string>? debug = null,
        Action<string>? warn = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _cancellationToken = cancellationToken;
        _debug = debug;
        _warn = warn;
    }

    /// <summary>Bytes currently held by this cache.</summary>
    public long CacheBytes => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Rendered bytes for one page, cached per (page, dpi). Cache-miss renders hold the write gate
    /// so concurrent askers (tile fill vs canvas) share one render instead of racing the cache;
    /// treatment callers (blank scan, probes, canvas) pass <paramref name="priority"/> so the
    /// process-wide pdfium gate lets them cut ahead of tile fills.
    /// </summary>
    public async Task<byte[]> GetPageBytesAsync(int pageIndex, int dpi, bool priority = false)
    {
        var key = (pageIndex, dpi);
        if (_renderedPages.TryGetValue(key, out byte[]? cached))
        {
            _debug?.Invoke($"预览缓存命中：第 {pageIndex + 1} 页，{dpi}dpi，{cached.Length} 字节");
            return cached;
        }

        long waitTicks = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        long gateMs = (long)Stopwatch.GetElapsedTime(waitTicks).TotalMilliseconds;
        try
        {
            if (_renderedPages.TryGetValue(key, out cached))
            {
                _debug?.Invoke($"预览缓存命中（等待闸门 {gateMs} ms）：第 {pageIndex + 1} 页，{dpi}dpi，{cached.Length} 字节");
                return cached;
            }

            int epoch = Volatile.Read(ref _epoch);
            long renderTicks = Stopwatch.GetTimestamp();
            byte[] bytes;
            try
            {
                bytes = await _renderer.RenderPageAsync(_file.Path, pageIndex, dpi, priority).ConfigureAwait(false);
            }
            catch (Exception) when (!_file.Exists)
            {
                // The user renamed/moved the file while the strip was open — track it down before
                // giving up; a plain failure here is what made the strip feel broken.
                if (!await Task.Run(_file.TryRelocate).ConfigureAwait(false)) throw;
                bytes = await _renderer.RenderPageAsync(_file.Path, pageIndex, dpi, priority).ConfigureAwait(false);
            }
            long renderMs = (long)Stopwatch.GetElapsedTime(renderTicks).TotalMilliseconds;
            _file.RememberStamp();
            // A page edit during the render invalidates these bytes; caching them now would
            // resurrect the pre-edit raster the edit's clear just dropped.
            if (Volatile.Read(ref _epoch) == epoch)
            {
                _renderedPages[key] = bytes;
                long cacheBytes = Interlocked.Add(ref _bytes, bytes.Length);
                if (cacheBytes > RenderCacheBudgetBytes)
                {
                    _warn?.Invoke($"预览渲染缓存超预算：清空前 {cacheBytes / 1048576.0:F1} MB" +
                                  $"（{cacheBytes} 字节），上限 {RenderCacheBudgetBytes / 1048576} MB");
                    _renderedPages.Clear();
                    Interlocked.Exchange(ref _bytes, 0);
                }
            }
            _debug?.Invoke($"预览缓存未命中：第 {pageIndex + 1} 页，{dpi}dpi，等待闸门 {gateMs} ms，" +
                           $"渲染 {renderMs} ms，{bytes.Length} 字节，优先级 {(priority ? "高" : "普通")}");
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one in-place PDF edit with the render gate held: readers wait for the write,
    /// then start from the edited file. Resolves a renamed file first.</summary>
    public async Task RunWriteAsync(Action edit)
    {
        long startTicks = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                _file.EnsureReadable();
                edit();
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        _debug?.Invoke($"预览写盘完成：{Path.GetFileName(_file.Path)}，" +
                       $"{(long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds} ms");
    }

    /// <summary>Overload for edits that return a value (the insert result).</summary>
    public async Task<T> RunWriteAsync<T>(Func<T> edit)
    {
        long startTicks = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            T result = await Task.Run(() =>
            {
                _file.EnsureReadable();
                return edit();
            }).ConfigureAwait(false);
            _debug?.Invoke($"预览写盘完成：{Path.GetFileName(_file.Path)}，" +
                           $"{(long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds} ms");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops every cached render — page indexes shifted or all page bytes changed.</summary>
    public void InvalidateAll()
    {
        Interlocked.Increment(ref _epoch);
        _renderedPages.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }

    /// <summary>Drops every cached render of one page (its bytes changed on disk).</summary>
    public void InvalidatePage(int pageIndex)
    {
        Interlocked.Increment(ref _epoch);
        foreach ((int page, int dpi) in _renderedPages.Keys.Where(key => key.Page == pageIndex).ToList())
            if (_renderedPages.TryRemove((page, dpi), out byte[]? bytes))
                Interlocked.Add(ref _bytes, -bytes.Length);
    }

    /// <summary>Drops one cache entry, used for probe rasters that are never displayed.</summary>
    public void InvalidateKey(int pageIndex, int dpi)
    {
        if (_renderedPages.TryRemove((pageIndex, dpi), out byte[]? bytes))
            Interlocked.Add(ref _bytes, -bytes.Length);
    }

    /// <summary>Remaps the cache around an insert: pages after the gap travel with their new
    /// index, so the strip does not have to re-render from scratch (the post-insert jolt).</summary>
    public void ShiftPages(int insertAfterIndex, int added)
    {
        var remapped = new Dictionary<(int Page, int Dpi), byte[]>();
        foreach (var ((page, dpi), bytes) in _renderedPages)
            remapped[(page <= insertAfterIndex ? page : page + added, dpi)] = bytes;
        Interlocked.Increment(ref _epoch); // in-flight pre-insert renders stay uncached
        _renderedPages.Clear();
        foreach (var (key, bytes) in remapped)
            _renderedPages[key] = bytes;
    }

    /// <summary>Drops the byte cache without invalidating in-flight renders (fold / off-screen).</summary>
    public void Trim()
    {
        _renderedPages.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }
}
