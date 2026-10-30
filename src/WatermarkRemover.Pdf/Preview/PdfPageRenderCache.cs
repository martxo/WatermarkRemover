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

    /// <summary>
    /// Ceiling across every open document. The per-document budget alone let a long session with
    /// several strips hold several times that (the log showed a multi-GB managed heap); when the
    /// total crosses this line the other documents hand their bytes back.
    /// </summary>
    internal static long ProcessBudgetBytes = 192L * 1024 * 1024;

    private static readonly object ProcessSync = new();
    private static readonly List<WeakReference<PdfPageRenderCache>> Live = new();

    /// <summary>
    /// Pages kept when a folded strip hands its bytes back. A wholesale clear made the first
    /// page after a reopen re-render (~130 ms at 150 dpi) even though the user had just been
    /// looking at it; keeping the most recently viewed pages makes the accordion instant.
    /// </summary>
    private const int TrimKeepPages = 8;

    private readonly IPdfPageRenderer _renderer;
    private readonly PdfDocumentFile _file;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<string>? _debug;
    private readonly Action<string>? _warn;

    private readonly ConcurrentDictionary<(int Page, int Dpi), byte[]> _renderedPages = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Recency order (most recent first) behind <see cref="Trim"/>. Guarded by
    /// <see cref="_recentSync" so reads and the fold-time trim never race.</summary>
    private readonly LinkedList<(int Page, int Dpi)> _recent = new();
    private readonly object _recentSync = new();
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

        // Weakly: a closed row's cache must be collectable even if nobody calls Trim first.
        lock (ProcessSync) Live.Add(new WeakReference<PdfPageRenderCache>(this));
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
            Touch(key);
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
                Touch(key);
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
                Touch(key);
                long cacheBytes = Interlocked.Add(ref _bytes, bytes.Length);
                if (cacheBytes > RenderCacheBudgetBytes)
                {
                    _warn?.Invoke($"预览渲染缓存超预算：清空前 {cacheBytes / 1048576.0:F1} MB" +
                                  $"（{cacheBytes} 字节），上限 {RenderCacheBudgetBytes / 1048576} MB");
                    ClearAll();
                }
                else
                {
                    EnforceProcessBudget(this);
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
        ClearAll();
    }

    /// <summary>Drops every cached byte and keeps the accounting truthful for both budgets.</summary>
    private void ClearAll()
    {
        _renderedPages.Clear();
        Interlocked.Exchange(ref _bytes, 0);
        lock (_recentSync) _recent.Clear();
    }

    /// <summary>Marks one page as most recently used — the fold-time trim keeps the newest.</summary>
    private void Touch((int Page, int Dpi) key)
    {
        lock (_recentSync)
        {
            LinkedListNode<(int Page, int Dpi)>? node = _recent.Find(key);
            if (node is null)
            {
                _recent.AddFirst(key);
                return;
            }

            _recent.Remove(node);
            _recent.AddFirst(key);
        }
    }

    /// <summary>Total bytes held by every live cache in this process.</summary>
    private static long LiveBytes()
    {
        long total = 0;
        lock (ProcessSync)
        {
            Live.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (WeakReference<PdfPageRenderCache> reference in Live)
            {
                if (reference.TryGetTarget(out PdfPageRenderCache? cache))
                {
                    total += Interlocked.Read(ref cache._bytes);
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Over the process budget: other documents release their caches (the one being read keeps
    /// its bytes). Creation order, oldest first — those are the rows the user has scrolled past.
    /// </summary>
    private void EnforceProcessBudget(PdfPageRenderCache keep)
    {
        if (LiveBytes() <= ProcessBudgetBytes) return;

        var others = new List<PdfPageRenderCache>();
        lock (ProcessSync)
        {
            Live.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (WeakReference<PdfPageRenderCache> reference in Live)
            {
                if (reference.TryGetTarget(out PdfPageRenderCache? cache) && !ReferenceEquals(cache, keep))
                {
                    others.Add(cache);
                }
            }
        }

        foreach (PdfPageRenderCache cache in others)
        {
            long released = Interlocked.Exchange(ref cache._bytes, 0);
            if (released > 0)
            {
                cache.ClearAll();
                _warn?.Invoke($"预览缓存总预算超限：释放 {Path.GetFileName(cache._file.Path)} " +
                              $"{released / 1048576.0:F1} MB（另一份文档正在使用）");
            }

            if (LiveBytes() <= ProcessBudgetBytes) break;
        }
    }

    /// <summary>Drops every cached render of one page (its bytes changed on disk).</summary>
    public void InvalidatePage(int pageIndex)
    {
        Interlocked.Increment(ref _epoch);
        foreach ((int page, int dpi) in _renderedPages.Keys.Where(key => key.Page == pageIndex).ToList())
            if (_renderedPages.TryRemove((page, dpi), out byte[]? bytes))
                Interlocked.Add(ref _bytes, -bytes.Length);

        lock (_recentSync)
        {
            LinkedListNode<(int Page, int Dpi)>? node = _recent.First;
            while (node is not null)
            {
                LinkedListNode<(int Page, int Dpi)>? next = node.Next;
                if (node.Value.Page == pageIndex) _recent.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>Drops one cache entry, used for probe rasters that are never displayed.</summary>
    public void InvalidateKey(int pageIndex, int dpi)
    {
        if (_renderedPages.TryRemove((pageIndex, dpi), out byte[]? bytes))
            Interlocked.Add(ref _bytes, -bytes.Length);

        lock (_recentSync)
        {
            LinkedListNode<(int Page, int Dpi)>? node = _recent.Find((pageIndex, dpi));
            if (node is not null) _recent.Remove(node);
        }
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
        lock (_recentSync) _recent.Clear(); // the old recency keys no longer exist
    }

    /// <summary>
    /// Folds a strip: hands back everything except the <see cref="TrimKeepPages"/> most recently
    /// viewed pages, so reopening paints instantly instead of re-rendering page one (~130 ms at
    /// 150 dpi). In-flight renders stay valid (no epoch bump) — they were never on screen.
    /// </summary>
    public void Trim()
    {
        var dropped = new List<(int Page, int Dpi)>();
        lock (_recentSync)
        {
            while (_recent.Count > TrimKeepPages)
            {
                LinkedListNode<(int Page, int Dpi)> last = _recent.Last!;
                dropped.Add(last.Value);
                _recent.RemoveLast();
            }
        }

        foreach ((int page, int dpi) in dropped)
            if (_renderedPages.TryRemove((page, dpi), out byte[]? bytes))
                Interlocked.Add(ref _bytes, -bytes.Length);
    }
}
