using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Scan;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Preview;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// Drives a PDF page set for both of its surfaces: the row's inline thumbnail grid and the
/// screen-level image viewer (film rail + canvas). One page shown at a time, previous / next
/// paging and a retry path, plus the tile collection — a tile click renders that page and asks
/// the host to open the viewer; the tile's corner button rotates the page 90° and saves that
/// straight into the PDF. The row and the viewer share this one instance, so renders, caches
/// and the rotation lock are single sources of truth.
/// Rendering goes through the shared <see cref="IPdfPageRenderer"/> (PDFium), so the preview
/// reuses the exact same PDF-to-image channel the OCR features already rely on.
/// </summary>
public sealed class PdfPreviewViewModel : ObservableObject
{
    /// <summary>
    /// Screen rendering DPI: an A4 page lands at roughly 1240×1754 — crisp on a monitor and far
    /// below the renderer's 2400 px long-side cap, so no downscaling cost.
    /// </summary>
    private const int PreviewDpi = 150;

    /// <summary>
    /// Thumbnail rendering DPI. 150 matches the main canvas so the shown page's tile reuses the
    /// cached render instead of paying for a second pass; dense scans stay legible at tile size.
    /// </summary>
    private const int ThumbnailDpi = 150;

    /// <summary>
    /// Render DPI for orientation probes only. Direction voting reads line-box layout, not
    /// small glyphs, so a 90dpi raster halves the pixels the OCR pays for with no accuracy
    /// loss. Tiles and canvas stay on 150 — this is a separate cache key and never displayed.
    /// </summary>
    private const int OrientationDpi = 90;

    /// <summary>
    /// Longest side a thumbnail is pre-scaled to. 720 keeps ~2× oversample even when
    /// the grid is zoomed to its 4.0 ceiling (368 px tile), so the tiles stay crisp at every
    /// zoom; the big downscale step from the 1718 px render happens here with a quality kernel,
    /// not in WPF's scaler.
    /// </summary>
    private const int ThumbMaxSide = 720;

    /// <summary>Base tile size at scale 1.0; the grid's zoom buttons multiply this.</summary>
    public const double BaseTileWidth = 92.0;
    public const double BaseTileHeight = 118.0;
    public const double MinThumbScale = 0.6;
    public const double MaxThumbScale = 4.0;
    private const double ThumbScaleStep = 0.15;

    private string _pdfPath;
    private readonly IPdfPageRenderer _renderer;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Host-supplied confirmation for the destructive page delete; null = allowed (tests).</summary>
    private readonly Func<int, bool>? _confirmPageDelete;

    /// <summary>Notified when the PDF was found under a new name after the user renamed it;
    /// the host keeps the row's output path in step. Null = nobody cares (tests).</summary>
    private readonly Action<string>? _onRelocated;

    /// <summary>The file stamp as last seen on disk — the identity a rename is tracked by
    /// (renaming changes neither size nor creation time). -1 before the first successful read.</summary>
    private long _knownLength = -1;
    private DateTime _knownCreationUtc;

    /// <summary>Content identity: SHA-256 of the file's first KiB. Size and creation time alone
    /// can collide (a temp directory full of same-batch scans); the header bytes do not — a
    /// rename keeps them, a different file almost surely has different ones.</summary>
    private byte[]? _fingerprint;

    /// <summary>OCR engine backing auto-orientation; null = the feature is unavailable.</summary>
    private readonly IOcrEngine? _ocrEngine;

    /// <summary>Whether orientation runs by itself after load (scan rows do; imported files don't).</summary>
    private readonly bool _autoOrient;

    /// <summary>Whether blank pages are dropped by themselves after load (scan rows do).</summary>
    private readonly bool _autoCleanBlank;

    /// <summary>Host-supplied scanner for the gap button: one scan pass, handed back as the
    /// written PDF's path (null = nothing scanned; throw = failure with a readable message).
    /// Null = this strip cannot insert scans (imported files) and the gaps never enable.</summary>
    private readonly Func<IProgress<string>, Task<string?>>? _scanProvider;

    private bool _isOrienting;

    /// <summary>Rendered pages kept as PNG bytes, keyed by (page, dpi); only the visible page gets decoded.
    /// Concurrent: the tile fill (background) and the canvas render (foreground) read and write it at
    /// the same time — a plain Dictionary threw under exactly that overlap and tiles went missing.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Page, int Dpi), byte[]> _renderedPages = new();

    /// <summary>Serializes cache-miss renders. Without it, the tile fill and the canvas can both
    /// miss the same (page, dpi) and pay for the page twice — and the native PDFium renderer
    /// must never be driven from two threads at once anyway.</summary>
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    /// <summary>
    /// Bumped whenever a page edit (rotate/delete/insert) makes rendered bytes stale. A render
    /// already in flight when the file changed still returns pre-edit pixels; without this epoch
    /// it would land in the cache AFTER the clear and both surfaces would keep showing the old
    /// page (the stale-cache class of bug the insert path hit in v1.15.8). Renders started under
    /// an older epoch return their bytes to their (already discarded) caller but never cache them.
    /// </summary>
    private int _cacheEpoch;

    private int _pageCount;
    private int _pageIndex;
    private int _displayedPageIndex = -1;
    private int _loadGeneration;
    private double _thumbnailScale = 1.0;
    private double _thumbnailScaleDraft = 1.0;
    private DispatcherTimer? _scaleSettleTimer;
    private ImageSource? _currentImage;
    private bool _isLoading;
    private bool _isRotating;
    private string _statusText = string.Empty;

    public PdfPreviewViewModel(
        string pdfPath, string displayName, IPdfPageRenderer renderer,
        CancellationToken cancellationToken = default,
        Func<int, bool>? confirmPageDelete = null,
        IOcrEngine? ocrEngine = null, bool autoOrient = false,
        Action<string>? onRelocated = null, bool autoCleanBlank = false,
        Func<IProgress<string>, Task<string?>>? scanProvider = null)
    {
        _pdfPath = pdfPath;
        _renderer = renderer;
        _cancellationToken = cancellationToken;
        _confirmPageDelete = confirmPageDelete;
        _ocrEngine = ocrEngine;
        _autoOrient = autoOrient;
        _onRelocated = onRelocated;
        _autoCleanBlank = autoCleanBlank;
        _scanProvider = scanProvider;
        Title = displayName;
        Thumbnails.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasThumbnails));
            OnPropertyChanged(nameof(CurrentTile));
        };
        PreviousCommand = new RelayCommand(() => _ = RenderAsync(PdfPreviewPaging.Clamp(_pageIndex - 1, _pageCount)), () => CanGoPrevious);
        NextCommand = new RelayCommand(() => _ = RenderAsync(PdfPreviewPaging.Clamp(_pageIndex + 1, _pageCount)), () => CanGoNext);
        RefreshCommand = new RelayCommand(() => _ = RenderAsync(_pageIndex, force: true));
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
        ZoomInCommand = new RelayCommand(
            () => ThumbnailScale = Math.Round(ThumbnailScale + ThumbScaleStep, 2),
            () => ThumbnailScale < MaxThumbScale - 0.001);
        ZoomOutCommand = new RelayCommand(
            () => ThumbnailScale = Math.Round(ThumbnailScale - ThumbScaleStep, 2),
            () => ThumbnailScale > MinThumbScale + 0.001);
        AutoOrientCommand = new RelayCommand(
            () => _ = AutoOrientAsync(),
            () => _ocrEngine is not null && !IsOrienting && !IsRotating && PageCount > 0);
        // Any document with at least one page accepts a scan at any slot — front, middle or
        // end. A one-page scan is exactly the case that needs the bookends most (the missing
        // page is usually the one before or after), so PageCount > 0, never > 1.
        InsertScanAfterCommand = new RelayCommand<int>(
            index => _ = InsertScanAfterAsync(index),
            _ => _scanProvider is not null && !IsRotating && !IsOrienting && PageCount > 0);
    }

    /// <summary>Multiplier on the base tile size, driven by the grid's zoom rail (0.6–2.0).</summary>
    public double ThumbnailScale
    {
        get => _thumbnailScale;
        set
        {
            double clamped = Math.Clamp(value, MinThumbScale, MaxThumbScale);
            if (!SetProperty(ref _thumbnailScale, clamped)) return;
            // The rail always shows the settled zoom, whatever moved it (button or drag).
            ApplyDraftScale(clamped);
            OnPropertyChanged(nameof(ThumbAreaMaxHeight));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// The zoom the rail is showing right now. The tiles follow it immediately, so the drag
    /// feels direct — but the strip's height (and with it the height of the whole record row)
    /// only catches up once the drag settles. Before that, every pixel of the drag resized the
    /// record list under the pointer, which is what made the page twitch.
    /// </summary>
    public double ThumbnailScaleDraft
    {
        get => _thumbnailScaleDraft;
        set
        {
            double clamped = Math.Clamp(value, MinThumbScale, MaxThumbScale);
            if (!ApplyDraftScale(clamped)) return;
            ScheduleScaleSettle();
        }
    }

    /// <summary>Pushes a zoom value into the rail and the tiles; false when nothing changed.</summary>
    private bool ApplyDraftScale(double value)
    {
        if (Math.Abs(value - _thumbnailScaleDraft) < 0.001) return false;
        _thumbnailScaleDraft = value;
        OnPropertyChanged(nameof(ThumbnailScaleDraft));
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(TileHeight));
        foreach (var thumbnail in Thumbnails)
            thumbnail.RefreshTileWidth();
        return true;
    }

    /// <summary>
    /// Starts the settle timer: when the rail stops moving for a moment, the settled zoom
    /// catches up with the draft and the strip's height follows.
    /// </summary>
    private void ScheduleScaleSettle()
    {
        if (_scaleSettleTimer is null)
        {
            _scaleSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(140),
            };
            _scaleSettleTimer.Tick += OnScaleSettled;
        }
        _scaleSettleTimer.Stop();
        _scaleSettleTimer.Start();
    }

    private void OnScaleSettled(object? sender, EventArgs e)
    {
        _scaleSettleTimer?.Stop();
        if (Math.Abs(_thumbnailScaleDraft - _thumbnailScale) < 0.001) return;
        ThumbnailScale = _thumbnailScaleDraft;
    }

    /// <summary>Live tile dimensions the shared tile template binds — the zoom the rail shows.</summary>
    public double TileWidth => Math.Round(BaseTileWidth * _thumbnailScaleDraft);
    public double TileHeight => Math.Round(BaseTileHeight * _thumbnailScaleDraft);

    /// <summary>Height cap for the inline thumbnail grid: about two tile rows, scrolling
    /// inside. A long document must not stretch the whole job list — the user scrolls the
    /// strip, the row itself (and the zoom rail beside it) stays a fixed size. It follows the
    /// settled zoom, so dragging the rail never resizes the row under the pointer.</summary>
    public const double ThumbAreaRows = 2.0;
    public double ThumbAreaMaxHeight => Math.Round(BaseTileHeight * _thumbnailScale) * ThumbAreaRows;

    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand AutoOrientCommand { get; }

    /// <summary>Gap button between two pages: scans fresh pages and splices them into the PDF
    /// right after the page the gap follows. Without a host scanner the gaps never enable.</summary>
    public RelayCommand<int> InsertScanAfterCommand { get; }

    /// <summary>Raised by the close button (and by Esc, handled by the viewer window) to leave
    /// the screen-level image viewer.</summary>
    public event EventHandler? RequestClose;

    /// <summary>
    /// Raised on a tile click: the host opens the screen-level viewer — full-screen dark veil,
    /// the page in the middle, the film-rail directory on the left (every desktop image
    /// browser's pattern).
    /// </summary>
    public event EventHandler? EnlargeRequested;

    /// <summary>File name shown in the overlay header; follows the file across a rename.</summary>
    public string Title { get; private set; }

    public int PageCount
    {
        get => _pageCount;
        private set
        {
            if (SetProperty(ref _pageCount, value))
            {
                RaisePagingChanged();
                // The gap buttons' CanExecute reads PageCount: a scan that just produced the
                // first pages must enable the plus slots right away, not on the next click.
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Zero-based index of the page being shown; updated as soon as a page is requested.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                RaisePagingChanged();
                OnPropertyChanged(nameof(CurrentTile));
                SyncThumbnailSelection();
            }
        }
    }

    /// <summary>
    /// The tile of the page currently on screen — the viewer's corner buttons reuse its rotate
    /// / delete commands, so both surfaces share one edit path (and one lock, one confirmation).
    /// Null before thumbnails exist.
    /// </summary>
    public PdfPreviewThumbnailViewModel? CurrentTile =>
        _pageIndex >= 0 && _pageIndex < Thumbnails.Count ? Thumbnails[_pageIndex] : null;

    public string PageIndicator => PdfPreviewPaging.FormatIndicator(_pageIndex, _pageCount);

    public bool CanGoPrevious => PdfPreviewPaging.CanGoPrevious(_pageIndex);

    public bool CanGoNext => PdfPreviewPaging.CanGoNext(_pageIndex, _pageCount);

    /// <summary>The rendered page, ready to bind to an Image control; null while empty or failed.</summary>
    public ImageSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Bottom strip tiles, one per page, built as soon as the page count is known.</summary>
    public ObservableCollection<PdfPreviewThumbnailViewModel> Thumbnails { get; } = new();

    /// <summary>What both strip surfaces actually render: the page tiles with a gap marker
    /// between each pair of adjacent pages (the insert-scan affordance). The tiles are the
    /// same instances as <see cref="Thumbnails"/> — selection, images and commands stay shared;
    /// gaps carry only the index to insert after.</summary>
    public ObservableCollection<object> StripItems { get; } = new();

    /// <summary>Drives the strip's visibility: no pages, no strip.</summary>
    public bool HasThumbnails => Thumbnails.Count > 0;

    /// <summary>
    /// True while a page-level edit (rotation or delete) is being written to the file. It
    /// disables every tile's edit buttons at once — PDF writes are single-flight, and a second
    /// click would race the first save.
    /// </summary>
    public bool IsRotating
    {
        get => _isRotating;
        private set
        {
            if (SetProperty(ref _isRotating, value))
            {
                foreach (var thumb in Thumbnails) thumb.RefreshCommands();
                // The gap buttons' CanExecute reads this flag, and WPF re-queries commands
                // only on some input events — without this nudge a finished scan left the
                // plus slots dead until the user's next click anywhere.
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>True while the OCR orientation pass is reading the document. Exposed as a
    /// notifying property (the gap buttons' CanExecute reads it) — a bare field left the
    /// buttons disabled after orientation finished until the next click re-queried commands.</summary>
    public bool IsOrienting
    {
        get => _isOrienting;
        private set
        {
            if (SetProperty(ref _isOrienting, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Live stage text ("正在渲染第 2 页…") or a readable failure reason. Never a popup.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(HasStatusText));
        }
    }

    public bool HasStatusText => !string.IsNullOrEmpty(_statusText);

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Reads the page count, then renders page 1. Failures surface in <see cref="StatusText"/>.</summary>
    public async Task LoadAsync()
    {
        if (IsCancelled) return;

        try
        {
            PageCount = await _renderer.GetPageCountAsync(_pdfPath);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开这份 PDF：{ex.Message}";
            return;
        }

        if (IsCancelled) return;

        if (PageCount == 0)
        {
            StatusText = "这份 PDF 没有可显示的页面。";
            return;
        }

        BuildThumbnails();
        await RenderAsync(0);

        // Scan rows self-heal as soon as the canvas is painted: the orientation verdict only
        // needs page one, so it must not queue behind the blank-page pass (whose renders are the
        // same 150dpi bitmaps the tiles need — that shared pass is why the turn used to start
        // only after the last thumbnail). Blank cleanup follows (or leads, when the first sheet
        // itself is blank) and its cached renders still feed the strip.
        if (_autoCleanBlank || (_autoOrient && _ocrEngine is not null))
            _ = RunScanPostProcessAsync();

        // Tiles fill in after the main canvas is already interactive; a tile that fails to render
        // simply stays empty — StatusText belongs to the big page, never to the strip. Tile
        // renders are LOW priority: treatment work (probes, blank scan, canvas) cuts ahead.
        _ = LoadThumbnailsAsync(Thumbnails.ToList());
    }

    /// <summary>
    /// The scan row's after-load pipeline. Serial by construction — both steps rewrite the same
    /// file and must not race. Ordering, though, is deliberate: the direction verdict only needs
    /// page one, so it runs as soon as the canvas is painted. Blank cleanup walks every page and
    /// its renders are exactly the rasters the strip needs, so running it first made the turn
    /// appear to start only after the last thumbnail. A blank first sheet is the one case that
    /// must wait: its verdict is meaningless and the page that should be probed (the first
    /// content page) only exists after the blank pages are dropped.
    /// </summary>
    private async Task RunScanPostProcessAsync()
    {
        if (!_autoOrient || _ocrEngine is null)
        {
            await CleanBlankPagesAsync();
            return;
        }

        bool blankFirstPage = _autoCleanBlank && await IsPageBlankAsync(0);

        if (blankFirstPage) await CleanBlankPagesAsync();

        await AutoOrientAsync();

        if (_autoCleanBlank && !blankFirstPage) await CleanBlankPagesAsync();
    }

    /// <summary>Blank verdict for one cached page; an unreadable page counts as content so the
    /// orientation probe (which is silent on unusable input) still gets its chance.</summary>
    private async Task<bool> IsPageBlankAsync(int pageIndex)
    {
        try
        {
            byte[] pagePng = await GetPageBytesAsync(pageIndex, PreviewDpi, priority: true);
            return await Task.Run(() => BlankPageDetector.IsBlank(pagePng));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops the blank pages a duplex scan produced. Judged from pixels (no OCR), deleted in one
    /// in-place save, and always leaving at least one page — even an all-blank scan keeps its
    /// first sheet rather than turning into nothing. Silent on failure: the scan result must
    /// never be held hostage by a helper step.
    /// </summary>
    internal async Task CleanBlankPagesAsync()
    {
        if (!_autoCleanBlank || _isOrienting || IsRotating || PageCount <= 1) return;

        IsRotating = true; // the single-writer lock: tiles' edit buttons idle while pages go
        try
        {
            var blankPages = new List<int>();
            for (int i = 0; i < PageCount; i++)
            {
                if (IsCancelled) return;
                byte[] png = await GetPageBytesAsync(i, PreviewDpi, priority: true);
                if (await Task.Run(() => BlankPageDetector.IsBlank(png)))
                    blankPages.Add(i);
            }
            if (IsCancelled) return;

            if (blankPages.Count >= PageCount)
            {
                blankPages.Remove(0); // an all-blank scan keeps its first sheet
            }
            if (blankPages.Count == 0) return;

            int removed = 0;
            await WritePdfAsync(() => removed = PdfPageRotator.DeletePages(_pdfPath, blankPages));
            if (IsCancelled || removed == 0) return;
            RememberFileStamp(force: true);
            AppLog.Info($"预览已自动删除空白页：{Path.GetFileName(_pdfPath)} 共 {removed} 页");

            // Indexes shifted for every page after the first removed one — the whole render
            // cache is keyed by index, so it all goes; the strip rebuilds below anyway.
            InvalidateAllPages();
            PageCount -= removed;
            BuildThumbnails();
            _ = LoadThumbnailsAsync(Thumbnails.ToList());
            await RenderAsync(Math.Min(PageIndex, PageCount - 1), force: true);
            StatusText = $"已自动删除 {removed} 页空白页";
        }
        catch (FileNotFoundException ex)
        {
            AppLog.Trace($"空白页清理中止（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            // A failed cleanup must never obscure the scan result — fold quietly.
            AppLog.Trace($"空白页清理失败：{Path.GetFileName(_pdfPath)}", ex);
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>Builds one tile per page and marks the first page as the one being shown.</summary>
    private void BuildThumbnails()
    {
        Thumbnails.Clear();
        for (int i = 0; i < _pageCount; i++)
            Thumbnails.Add(new PdfPreviewThumbnailViewModel(this, i));
        SyncThumbnailSelection();
        RebuildStripItems();
    }

    /// <summary>Rebuilds the display sequence: one atomic unit per page — [gap | tile | tail].
    /// Unit i opens with the gap after page i-1 (-1 = in front of page one) and only the last
    /// unit closes with the trailing gap, so every slot of the document (front, between, end)
    /// still takes a scan while the wrap panel can only break between units — a wrapped row
    /// never starts with a bare tile, and the columns line up across rows.</summary>
    private void RebuildStripItems()
    {
        StripItems.Clear();
        for (int i = 0; i < Thumbnails.Count; i++)
        {
            string gapToolTip = i == 0
                ? "点击扫描，把新页插到第一页前面"
                : "点击扫描，把新页插到这两页之间";
            var tail = i == Thumbnails.Count - 1
                ? new PdfPreviewGapViewModel(i, "点击扫描，把新页接着扫到最后一页后面")
                : null;
            StripItems.Add(new PdfPreviewStripUnitViewModel(
                new PdfPreviewGapViewModel(i - 1, gapToolTip), Thumbnails[i], tail));
        }
    }

    /// <summary>Exactly one tile carries the accent ring: the page the canvas is showing.</summary>
    private void SyncThumbnailSelection()
    {
        foreach (var thumb in Thumbnails)
            thumb.IsSelected = thumb.Index == _pageIndex;
    }

    /// <summary>Fills the strip tile by tile, off the UI path, without ever touching StatusText.</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<PdfPreviewThumbnailViewModel> thumbs)
    {
        foreach (var thumb in thumbs)
        {
            // Re-supplied tiles after a reload belong to a superseded session: drop out.
            if (IsCancelled || !Thumbnails.Contains(thumb)) return;

            try
            {
                byte[] bytes = await GetPageBytesAsync(thumb.Index, ThumbnailDpi);
                if (IsCancelled || !Thumbnails.Contains(thumb)) return;
                thumb.SetImage(await Task.Run(() => MakeThumbSource(bytes)));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // The main canvas reports the real reason; an empty tile is enough here.
            }
        }
    }

    /// <summary>
    /// Tile click: the page renders (so the viewer opens on it instantly, from the shared cache)
    /// and the host is asked for the screen-level viewer — the image-browser interaction.
    /// </summary>
    internal void SelectPage(int pageIndex)
    {
        _ = RenderAsync(pageIndex);
        EnlargeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Tile corner button: turn the page a quarter turn clockwise, save that into the PDF
    /// immediately, then refresh whatever shows the page — the tile, and the canvas when this is
    /// the page on screen. Any failure leaves the previous orientation (the file is swapped only
    /// after a complete save) and reports the reason in <see cref="StatusText"/>.
    /// </summary>
    internal async Task RotatePageAsync(int pageIndex)
    {
        if (IsCancelled || IsRotating) return;
        if (pageIndex < 0 || pageIndex >= PageCount) return;

        IsRotating = true;
        StatusText = $"正在旋转第 {pageIndex + 1} 页并保存…";
        try
        {
            await Task.Run(() =>
            {
                EnsurePdfReadable();
                PdfPageRotator.RotateClockwise(_pdfPath, pageIndex);
            });
            InvalidatePage(pageIndex);
            AppLog.Info($"预览旋转已保存：{Path.GetFileName(_pdfPath)} 第 {pageIndex + 1} 页");

            if (IsCancelled) return; // the file is saved; nothing is on screen any more
            RememberFileStamp(force: true);

            byte[] thumbBytes = await GetPageBytesAsync(pageIndex, ThumbnailDpi, priority: true);
            if (IsCancelled) return;
            if (pageIndex < Thumbnails.Count)
                Thumbnails[pageIndex].SetImage(await Task.Run(() => MakeThumbSource(thumbBytes)));

            if (PageIndex == pageIndex)
                await RenderAsync(pageIndex, force: true);

            // Only clear our own progress line; a render failure reported by RenderAsync must stay.
            if (StatusText.StartsWith("正在旋转", StringComparison.Ordinal))
                StatusText = string.Empty;
        }
        catch (FileNotFoundException ex)
        {
            AppLog.Trace($"预览旋转失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Trace($"预览旋转失败：{Path.GetFileName(_pdfPath)} 第 {pageIndex + 1} 页", ex);
            if (!IsCancelled) StatusText = $"旋转保存失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// Tile delete button: remove that page from the PDF right away (after the host confirms),
    /// rebuild the strip with the shifted indexes and show the page that took the deleted one's
    /// place. The last remaining page is never deletable — a zero-page PDF is not a valid
    /// document, so the strip keeps it and the command is disabled on that tile.
    /// </summary>
    internal async Task DeletePageAsync(int pageIndex)
    {
        if (IsCancelled || IsRotating) return;
        if (pageIndex < 0 || pageIndex >= PageCount) return;
        if (PageCount <= 1) return; // the last page must survive: empty PDFs cannot be opened
        if (_confirmPageDelete is { } confirm && !confirm(pageIndex)) return;

        IsRotating = true;
        StatusText = $"正在删除第 {pageIndex + 1} 页…";
        try
        {
            await Task.Run(() =>
            {
                EnsurePdfReadable();
                PdfPageRotator.DeletePage(_pdfPath, pageIndex);
            });
            AppLog.Info($"预览已删除页：{Path.GetFileName(_pdfPath)} 第 {pageIndex + 1} 页");

            if (IsCancelled) return; // the file is saved; nothing is on screen any more
            RememberFileStamp(force: true);

            // Every page after the deleted one shifts down an index — the render cache keys by
            // index, so all of it must go, not just the deleted page's entries.
            InvalidateAllPages();
            PageCount--;
            BuildThumbnails();
            _ = LoadThumbnailsAsync(Thumbnails.ToList());

            // The page that took the deleted one's place becomes the shown page.
            await RenderAsync(Math.Min(pageIndex, PageCount - 1), force: true);
            if (StatusText.StartsWith("正在删除", StringComparison.Ordinal))
                StatusText = string.Empty;
        }
        catch (FileNotFoundException ex)
        {
            AppLog.Trace($"预览删除页失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Trace($"预览删除页失败：{Path.GetFileName(_pdfPath)} 第 {pageIndex + 1} 页", ex);
            if (!IsCancelled) StatusText = $"删除页失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// Waits until no load / orientation / page-write pass is touching the file. The host's
    /// background naming (a rename = File.Move) races those writers otherwise — a move during
    /// a strip rewrite throws and the scan silently keeps its timestamp name.
    /// </summary>
    internal async Task WaitForSettledAsync()
    {
        for (int i = 0; i < 300 && (_isLoading || _isOrienting || IsRotating); i++)
            await Task.Delay(100).ConfigureAwait(false);
    }

    /// <summary>
    /// Gap button between two pages: one scan pass (the host supplies the scanner and reports
    /// progress through <see cref="StatusText"/>), then the scanned pages are spliced into the
    /// PDF right after the page the gap follows — one in-place save, then the strip and the
    /// canvas rebuild onto the new arrangement with the first inserted page on screen. The
    /// scan's intermediate file is removed once its pages live inside the target; a failed
    /// scan or a failed insert keeps it, so nothing is ever lost.
    /// </summary>
    internal async Task InsertScanAfterAsync(int insertAfterIndex)
    {
        if (IsCancelled || IsRotating || IsOrienting) return;
        if (_scanProvider is null) return;
        if (insertAfterIndex < -1 || insertAfterIndex >= PageCount) return; // -1 = in front of page one

        IsRotating = true;
        StatusText = "正在准备扫描…";
        string slot = insertAfterIndex < 0 ? "开头" : $"第 {insertAfterIndex + 1} 页后";
        string? scannedPath = null;
        bool inserted = false;
        try
        {
            scannedPath = await _scanProvider(new Progress<string>(message => StatusText = message)).ConfigureAwait(true);
            if (IsCancelled) return;
            if (scannedPath is null || !File.Exists(scannedPath))
            {
                StatusText = "扫描未完成，没有页面可插入。";
                return;
            }

            int added = await Task.Run(() =>
            {
                EnsurePdfReadable();
                return PdfPageInserter.InsertAfter(_pdfPath, insertAfterIndex, scannedPath!);
            }).ConfigureAwait(true);
            inserted = added > 0;
            AppLog.Info($"预览已插入扫描页：{Path.GetFileName(_pdfPath)} {slot}新增 {added} 页");

            if (IsCancelled) return; // the file is saved; nothing is on screen any more
            ShiftRenderedPages(insertAfterIndex, added);
            PageCount += added;

            // Fresh scanner pages can sit sideways or upside down whatever the document looked
            // like before the gap — probe each new page and turn the crooked ones in place,
            // before the strip below paints them.
            await OrientInsertedPagesAsync(insertAfterIndex + 1, added).ConfigureAwait(true);
            if (IsCancelled) return; // pages are fixed in the file; nothing is on screen any more
            RememberFileStamp(force: true);

            // Grow the strip in place: tiles before the gap keep their page, tiles after it are
            // re-pointed with a notification, and only the scanner's pages need pixels. The old
            // clear-and-rebuild threw away every tile and every cached render, so the whole
            // strip blanked and repainted page by page — the post-insert jolt.
            int firstMoved = insertAfterIndex + 1;
            for (int i = firstMoved; i < Thumbnails.Count; i++)
                Thumbnails[i].SetIndex(i + added);
            var fresh = new List<PdfPreviewThumbnailViewModel>();
            for (int i = 0; i < added; i++)
            {
                var tile = new PdfPreviewThumbnailViewModel(this, firstMoved + i);
                fresh.Add(tile);
                Thumbnails.Insert(firstMoved + i, tile);
            }
            SyncThumbnailSelection();
            foreach (var thumb in Thumbnails) thumb.RefreshCommands();
            RebuildStripItems();
            _ = LoadThumbnailsAsync(fresh);

            // Land on the first page that came out of the scanner.
            await RenderAsync(Math.Min(insertAfterIndex + 1, PageCount - 1), force: true).ConfigureAwait(true);

            // RenderAsync clears StatusText on success; write our line after that, and leave a
            // render failure's own message standing.
            if (StatusText.Length == 0) StatusText = $"已插入 {added} 页扫描页。";
        }
        catch (FileNotFoundException ex)
        {
            AppLog.Trace($"插入扫描页失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Trace($"插入扫描页失败：{Path.GetFileName(_pdfPath)} {slot}", ex);
            if (!IsCancelled) StatusText = $"插入扫描页失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
            if (inserted && scannedPath is not null && File.Exists(scannedPath))
            {
                try { File.Delete(scannedPath); }
                catch (IOException)
                {
                    // A leftover scan file beats losing the pages; the log already recorded the insert.
                }
            }
        }
    }

    /// <summary>
    /// Auto-orientation scoped to the pages an insert just brought in: each new page is
    /// rendered and voted on by the same OCR quarter-turn detector the load-time orient uses;
    /// every crooked page is collected first, then all turns land in ONE in-place save. Quiet
    /// by contract — a page the engine cannot read (blank back, unusable OCR) stays untouched.
    /// </summary>
    private async Task OrientInsertedPagesAsync(int firstIndex, int count)
    {
        if (_ocrEngine is null) return;
        var detector = new PdfOrientationDetector(_ocrEngine);

        // Probe first, write once: every fresh page is OCR-scored at probe DPI, then all pages
        // that need turning are applied in a single file rewrite. One rewrite per page used to
        // make an N-page insert rewrite the whole PDF N times. Cancelling mid-probe now simply
        // leaves the file untouched — a cleaner contract than mid-way partial turns.
        var turns = new List<(int Index, int Steps)>();
        var probed = new List<int>();
        for (int index = firstIndex; index < firstIndex + count && index < PageCount; index++)
        {
            byte[] pagePng = await GetPageBytesAsync(index, OrientationDpi, priority: true).ConfigureAwait(true);
            if (IsCancelled) return;
            probed.Add(index);
            int steps = await Task.Run(() => detector.DetectClockwiseStepsAsync(pagePng, _cancellationToken)).ConfigureAwait(true);
            if (IsCancelled) return;
            if (steps != 0) turns.Add((index, steps));
        }

        // Probe rasters are never displayed (tiles and canvas read PreviewDpi); unturned
        // pages' entries are pure ballast, turned pages lose their whole key set below.
        foreach (int index in probed.Where(i => turns.All(t => t.Index != i)))
            _renderedPages.TryRemove((index, OrientationDpi), out _);

        if (turns.Count == 0) return;

        await Task.Run(() =>
        {
            EnsurePdfReadable();
            PdfPageRotator.RotatePages(_pdfPath, turns);
        }).ConfigureAwait(true);
        if (IsCancelled) return; // the file is fixed; nothing is on screen any more

        // The probes cached these pages' PRE-rotation bytes and the canvas may hold a pre-turn
        // PreviewDpi render; drop every stale entry so both surfaces show the turned page.
        foreach (var (index, _) in turns)
            InvalidatePage(index);

        AppLog.Info($"已摆正插入页：{Path.GetFileName(_pdfPath)}，{turns.Count}/{count} 页需旋转" +
                    $"（{string.Join("、", turns.Select(t => $"第{t.Index + 1}页顺时针{t.Steps * 90}°"))}），单次写盘");
    }

    /// <summary>
    /// Auto-orientation: render the first page, let the OCR engine vote on the four quarter
    /// turns, and — if the verdict is decisive — turn the whole document in a single save, then
    /// rebuild every surface (tiles, render cache, canvas). Deliberately quiet: an engine that
    /// can't read the page (blank page, unusable OCR) keeps the file untouched and reports
    /// nothing; the button stays for a manual retry.
    /// </summary>
    internal async Task AutoOrientAsync()
    {
        if (IsCancelled || _ocrEngine is null || IsOrienting) return;
        if (PageCount <= 0 || _isLoading) return; // let the initial paint finish first

        IsOrienting = true;
        StatusText = "正在检测页面方向…";
        try
        {
            byte[] pagePng = await GetPageBytesAsync(0, OrientationDpi, priority: true);

            var detector = new PdfOrientationDetector(_ocrEngine);
            int steps = await Task.Run(() => detector.DetectClockwiseStepsAsync(pagePng, _cancellationToken));
            _renderedPages.TryRemove((0, OrientationDpi), out _); // probe raster is never displayed
            if (IsCancelled) return;

            if (steps == 0)
            {
                if (StatusText.StartsWith("正在检测", StringComparison.Ordinal)) StatusText = string.Empty;
                AppLog.Info($"方向检测：{Path.GetFileName(_pdfPath)} 已是正确朝向，未改动");
                return;
            }

            await WritePdfAsync(() => PdfPageRotator.RotateAll(_pdfPath, steps));
            if (IsCancelled) return; // the file is fixed; nothing is on screen any more
            RememberFileStamp(force: true);

            AppLog.Info($"已自动摆正：{Path.GetFileName(_pdfPath)}，顺时针 {steps * 90}°，共 {PageCount} 页");

            // Every /Rotate changed but the content streams did not — only the rotation metadata
            // is stale in the caches, yet page bytes differ per (page, dpi) key, so drop it all.
            InvalidateAllPages();
            BuildThumbnails();
            _ = LoadThumbnailsAsync(Thumbnails.ToList());
            await RenderAsync(PageIndex, force: true);

            // RenderAsync clears StatusText on success; write our line after that, and leave a
            // render failure's own message standing.
            if (StatusText.Length == 0) StatusText = "已自动摆正。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Auto-orientation is a convenience: failures stay out of the user's way (the file
            // itself is untouched — RotateAll swaps only after a complete save).
            AppLog.Trace($"自动摆正失败：{Path.GetFileName(_pdfPath)}", ex);
            if (!IsCancelled) StatusText = string.Empty;
        }
        finally
        {
            IsOrienting = false;
        }
    }

    /// <summary>
    /// Renders one page and shows it. Revisiting an already rendered page skips the renderer
    /// entirely; a newer request always wins, so rapid paging cannot show a stale page.
    /// </summary>
    private async Task RenderAsync(int pageIndex, bool force = false)
    {
        if (pageIndex < 0 || pageIndex >= _pageCount) return;
        if (!force && pageIndex == _displayedPageIndex && CurrentImage is not null) return;

        int generation = ++_loadGeneration;
        PageIndex = pageIndex;
        IsLoading = true;
        StatusText = $"正在渲染第 {pageIndex + 1} 页…";

        try
        {
            byte[] bytes = await GetPageBytesAsync(pageIndex, PreviewDpi, priority: true);
            if (IsCancelled || generation != _loadGeneration) return;

            CurrentImage = Decode(bytes);
            _displayedPageIndex = pageIndex;
            StatusText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (IsCancelled || generation != _loadGeneration) return;

            _displayedPageIndex = -1;
            CurrentImage = null;
            StatusText = $"预览失败：{ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    /// <summary>Remaps the render cache around an insert: pages at or before the gap keep their
    /// pixels, pages after it travel with their new index. Clearing the cache instead used to
    /// throw away every render, so the whole strip had to render again from scratch — the
    /// blank-and-repaint jolt right after an insert.</summary>
    private void ShiftRenderedPages(int insertAfterIndex, int added)
    {
        var remapped = new Dictionary<(int Page, int Dpi), byte[]>();
        foreach (var ((page, dpi), bytes) in _renderedPages)
            remapped[(page <= insertAfterIndex ? page : page + added, dpi)] = bytes;
        Interlocked.Increment(ref _cacheEpoch); // in-flight pre-insert renders stay uncached
        _renderedPages.Clear();
        foreach (var (key, bytes) in remapped)
            _renderedPages[key] = bytes;
    }

    /// <summary>Rendered bytes are cached per (page, dpi) — a page the user already visited never re-renders.
    /// Cache-miss renders hold <see cref="_renderGate"/> so concurrent askers (tile fill vs canvas)
    /// share one render instead of racing the cache and doubling the work. Treatment callers
    /// (blank scan, probes, canvas) pass <paramref name="priority"/> to jump the tile queue in the
    /// process-wide pdfium gate.</summary>
    private async Task<byte[]> GetPageBytesAsync(int pageIndex, int dpi, bool priority = false)
    {
        var key = (pageIndex, dpi);
        if (_renderedPages.TryGetValue(key, out byte[]? cached)) return cached;

        await _renderGate.WaitAsync(_cancellationToken);
        try
        {
            if (_renderedPages.TryGetValue(key, out cached)) return cached;

            int epoch = Volatile.Read(ref _cacheEpoch);
            byte[] bytes;
            try
            {
                bytes = await _renderer.RenderPageAsync(_pdfPath, pageIndex, dpi, priority);
            }
            catch (Exception) when (!File.Exists(_pdfPath))
            {
                // The user renamed/moved the file while the strip was open — track it down before
                // giving up; a plain failure here is what made the strip feel broken.
                if (!await Task.Run(TryRelocatePdf)) throw;
                bytes = await _renderer.RenderPageAsync(_pdfPath, pageIndex, dpi, priority);
            }
            RememberFileStamp();
            // A page edit during the render invalidates these bytes; caching them now would
            // resurrect the pre-edit raster the edit's clear just dropped.
            if (Volatile.Read(ref _cacheEpoch) == epoch)
                _renderedPages[key] = bytes;
            return bytes;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>
    /// Finds the PDF again after the user renamed or moved it while the strip was open. A
    /// rename changes neither size nor creation time, so the remembered stamp identifies the
    /// file in its (unchanged) directory — but only when the match is unique: guessing between
    /// several same-shaped files would risk editing the wrong document. Returns false when the
    /// file is gone for good or cannot be told apart.
    /// </summary>
    private bool TryRelocatePdf()
    {
        if (File.Exists(_pdfPath)) return true;

        string? directory = Path.GetDirectoryName(_pdfPath);
        if (directory is null || !Directory.Exists(directory) || _knownLength < 0 || _fingerprint is null)
            return false;

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "*.pdf")
                .Where(p => !string.Equals(p, _pdfPath, StringComparison.OrdinalIgnoreCase))
                .Where(p =>
                {
                    try
                    {
                        var info = new FileInfo(p);
                        return info.Length == _knownLength && info.CreationTimeUtc == _knownCreationUtc;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                })
                .Where(HeadBytesMatch) // the stamp narrows the field; content picks the file
                .ToArray();
        }
        catch (IOException)
        {
            return false;
        }

        if (candidates.Length != 1) return false;

        _pdfPath = candidates[0];
        Title = Path.GetFileName(_pdfPath);
        OnPropertyChanged(nameof(Title));
        AppLog.Info($"预览已跟踪改名文件：{Path.GetFileName(_pdfPath)}");
        _onRelocated?.Invoke(_pdfPath);
        return true;
    }

    /// <summary>True when the file's first KiB hash the one remembered — the rename-proof
    /// content check that keeps a same-sized sibling scan from being mistaken for our file.</summary>
    private bool HeadBytesMatch(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[1024];
            int read = stream.Read(head);
            return System.Security.Cryptography.SHA256.HashData(head[..read])
                .AsSpan().SequenceEqual(_fingerprint);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Records the file stamp a future rename will be matched against; called after
    /// every successful read or write, since edits change the size (pass force to re-hash then).</summary>
    private void RememberFileStamp(bool force = false)
    {
        try
        {
            if (!File.Exists(_pdfPath)) return;
            var info = new FileInfo(_pdfPath);
            if (!force && info.Length == _knownLength && _fingerprint is not null) return;
            _knownLength = info.Length;
            _knownCreationUtc = info.CreationTimeUtc;
            using var stream = File.OpenRead(_pdfPath);
            Span<byte> head = stackalloc byte[1024];
            int read = stream.Read(head);
            _fingerprint = System.Security.Cryptography.SHA256.HashData(head[..read]);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Runs one in-place PDF edit with the render gate held: the strip's tile fill is live while
    /// the automatic passes run, and swapping the file under an in-flight pdfium read both risks
    /// a sharing violation and lets pre-edit pixels reach the cache. Reads wait for the write,
    /// then start from the edited file.
    /// </summary>
    private async Task WritePdfAsync(Action edit)
    {
        await _renderGate.WaitAsync(_cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                EnsurePdfReadable();
                edit();
            });
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>Edit guard: resolves a renamed file first, then fails with a message the user
    /// can act on instead of a raw path error.</summary>
    private void EnsurePdfReadable()
    {
        if (!TryRelocatePdf())
            throw new FileNotFoundException("文件已被移动或重命名，且无法自动找回。", _pdfPath);
        RememberFileStamp();
    }

    /// <summary>Drops every cached render — used when page indexes shift or every page's bytes
    /// change (blank deletion, page deletion, document-wide rotation). Bumps the epoch so a
    /// render that was already in flight cannot write pre-edit pixels back into the fresh cache.</summary>
    private void InvalidateAllPages()
    {
        Interlocked.Increment(ref _cacheEpoch);
        _renderedPages.Clear();
    }

    /// <summary>Drops every cached render of a page — used after its bytes changed on disk (rotation).</summary>
    private void InvalidatePage(int pageIndex)
    {
        Interlocked.Increment(ref _cacheEpoch);
        foreach ((int page, int dpi) in _renderedPages.Keys.Where(key => key.Page == pageIndex).ToList())
            _renderedPages.TryRemove((page, dpi), out _);
    }

    /// <summary>PDFium hands back PNG bytes; WPF wants a frozen bitmap it can bind from any thread.</summary>
    private static ImageSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Turns a full render into a frozen tile image. The caller runs this OFF the UI thread:
    /// PDFium's PNG is decoded once by Skia, pre-scaled to <see cref="ThumbMaxSide"/> with a
    /// quality resampler, and wrapped straight into a frozen BitmapSource from its raw BGRA
    /// pixels. The old pipeline re-encoded the scaled bitmap as a quality-100 PNG only for
    /// WPF to immediately decode that PNG again — both steps pure waste, and both ran on the
    /// dispatcher once per page right when a scan finished: the post-scan stutter.
    /// Sources already at or below the cap skip the scale (tests feed tiny images) but are
    /// still converted, so the returned image is always frozen and thread-safe.
    /// </summary>
    private static ImageSource MakeThumbSource(byte[] png)
    {
        using var source = SKBitmap.Decode(png);
        if (source is null) return Decode(png);

        int longest = Math.Max(source.Width, source.Height);
        using var target = longest > ThumbMaxSide ? Downscale(source, longest) : ConvertBgra(source);
        return ToFrozenBitmap(target);
    }

    /// <summary>Quality downscale straight into WPF-eatable BGRA pixels.</summary>
    private static SKBitmap Downscale(SKBitmap source, int longest)
    {
        double factor = ThumbMaxSide / (double)longest;
        int w = Math.Max(1, (int)Math.Round(source.Width * factor));
        int h = Math.Max(1, (int)Math.Round(source.Height * factor));
        return ScaleInto(w, h, source);
    }

    /// <summary>Same-size BGRA copy: the source color type may not match WPF's Bgra32.</summary>
    private static SKBitmap ConvertBgra(SKBitmap source) =>
        ScaleInto(source.Width, source.Height, source);

    private static SKBitmap ScaleInto(int w, int h, SKBitmap source)
    {
        var target = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (!source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell)))
        {
            target.Dispose();
            return new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        }
        return target;
    }

    private static BitmapSource ToFrozenBitmap(SKBitmap bgra)
    {
        var bitmap = BitmapSource.Create(
            bgra.Width, bgra.Height, 96, 96,
            PixelFormats.Bgra32, null, bgra.Bytes.ToArray(), bgra.RowBytes);
        bitmap.Freeze();
        return bitmap;
    }

    private bool IsCancelled => _cancellationToken.IsCancellationRequested;

    private void RaisePagingChanged()
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }
}
