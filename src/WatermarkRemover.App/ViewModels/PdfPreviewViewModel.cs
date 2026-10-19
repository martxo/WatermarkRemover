using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private bool _isOrienting;

    /// <summary>Rendered pages kept as PNG bytes, keyed by (page, dpi); only the visible page gets decoded.</summary>
    private readonly Dictionary<(int Page, int Dpi), byte[]> _renderedPages = new();

    private int _pageCount;
    private int _pageIndex;
    private int _displayedPageIndex = -1;
    private int _loadGeneration;
    private double _thumbnailScale = 1.0;
    private ImageSource? _currentImage;
    private bool _isLoading;
    private bool _isRotating;
    private string _statusText = string.Empty;

    public PdfPreviewViewModel(
        string pdfPath, string displayName, IPdfPageRenderer renderer,
        CancellationToken cancellationToken = default,
        Func<int, bool>? confirmPageDelete = null,
        IOcrEngine? ocrEngine = null, bool autoOrient = false,
        Action<string>? onRelocated = null, bool autoCleanBlank = false)
    {
        _pdfPath = pdfPath;
        _renderer = renderer;
        _cancellationToken = cancellationToken;
        _confirmPageDelete = confirmPageDelete;
        _ocrEngine = ocrEngine;
        _autoOrient = autoOrient;
        _onRelocated = onRelocated;
        _autoCleanBlank = autoCleanBlank;
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
            () => _ocrEngine is not null && !_isOrienting && !IsRotating && PageCount > 0);
    }

    /// <summary>Multiplier on the base tile size, driven by the grid's zoom rail (0.6–2.0).</summary>
    public double ThumbnailScale
    {
        get => _thumbnailScale;
        set
        {
            if (SetProperty(ref _thumbnailScale, Math.Clamp(value, MinThumbScale, MaxThumbScale)))
            {
                OnPropertyChanged(nameof(TileWidth));
                OnPropertyChanged(nameof(TileHeight));
                OnPropertyChanged(nameof(ThumbAreaMaxHeight));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Live tile dimensions the shared tile template binds.</summary>
    public double TileWidth => Math.Round(BaseTileWidth * _thumbnailScale);
    public double TileHeight => Math.Round(BaseTileHeight * _thumbnailScale);

    /// <summary>Height cap for the inline thumbnail grid: about two tile rows, scrolling
    /// inside. A long document must not stretch the whole job list — the user scrolls the
    /// strip, the row itself (and the zoom rail beside it) stays a fixed size.</summary>
    public const double ThumbAreaRows = 2.0;
    public double ThumbAreaMaxHeight => TileHeight * ThumbAreaRows;

    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand AutoOrientCommand { get; }

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
            if (SetProperty(ref _pageCount, value)) RaisePagingChanged();
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

    /// <summary>True while the orientation pass (detect + rewrite) is running; tests drain on it.</summary>
    internal bool IsOrienting => _isOrienting;

    /// <summary>Bottom strip tiles, one per page, built as soon as the page count is known.</summary>
    public ObservableCollection<PdfPreviewThumbnailViewModel> Thumbnails { get; } = new();

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
                foreach (var thumb in Thumbnails) thumb.RefreshCommands();
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

        // Tiles fill in after the main canvas is already interactive; a tile that fails to render
        // simply stays empty — StatusText belongs to the big page, never to the strip.
        _ = LoadThumbnailsAsync(Thumbnails.ToList());

        // Scan rows self-heal after the first paint, fire-and-forget and in a fixed order:
        // first the blank pages a duplex feeder produced are dropped (they must not take part
        // in any later step), then orientation is corrected on what remains.
        if (_autoCleanBlank || (_autoOrient && _ocrEngine is not null))
            _ = RunScanPostProcessAsync();
    }

    /// <summary>The scan row's after-load pipeline: blank-page cleanup, then auto-orientation.
    /// Serial by construction — both steps rewrite the same file and must not race.</summary>
    private async Task RunScanPostProcessAsync()
    {
        await CleanBlankPagesAsync();
        if (_autoOrient && _ocrEngine is not null)
            await AutoOrientAsync();
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
                byte[] png = await GetPageBytesAsync(i, PreviewDpi);
                if (await Task.Run(() => BlankPageDetector.IsBlank(png)))
                    blankPages.Add(i);
            }
            if (IsCancelled) return;

            if (blankPages.Count >= PageCount)
            {
                blankPages.Remove(0); // an all-blank scan keeps its first sheet
            }
            if (blankPages.Count == 0) return;

            int removed = await Task.Run(() => PdfPageRotator.DeletePages(_pdfPath, blankPages));
            if (IsCancelled || removed == 0) return;
            RememberFileStamp(force: true);
            AppLog.Info($"预览已自动删除空白页：{Path.GetFileName(_pdfPath)} 共 {removed} 页");

            // Indexes shifted for every page after the first removed one — the whole render
            // cache is keyed by index, so it all goes; the strip rebuilds below anyway.
            _renderedPages.Clear();
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
                thumb.SetImage(Decode(MakeThumb(bytes)));
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

            byte[] thumbBytes = await GetPageBytesAsync(pageIndex, ThumbnailDpi);
            if (IsCancelled) return;
            if (pageIndex < Thumbnails.Count)
                Thumbnails[pageIndex].SetImage(Decode(MakeThumb(thumbBytes)));

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
            _renderedPages.Clear();
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
    /// Auto-orientation: render the first page, let the OCR engine vote on the four quarter
    /// turns, and — if the verdict is decisive — turn the whole document in a single save, then
    /// rebuild every surface (tiles, render cache, canvas). Deliberately quiet: an engine that
    /// can't read the page (blank page, unusable OCR) keeps the file untouched and reports
    /// nothing; the button stays for a manual retry.
    /// </summary>
    internal async Task AutoOrientAsync()
    {
        if (IsCancelled || _ocrEngine is null || _isOrienting) return;
        if (PageCount <= 0 || _isLoading) return; // let the initial paint finish first

        _isOrienting = true;
        StatusText = "正在检测页面方向…";
        try
        {
            byte[] pagePng = await GetPageBytesAsync(0, PreviewDpi);
            if (IsCancelled) return;

            var detector = new PdfOrientationDetector(_ocrEngine);
            int steps = await Task.Run(() => detector.DetectClockwiseStepsAsync(pagePng, _cancellationToken));
            if (IsCancelled) return;

            if (steps == 0)
            {
                if (StatusText.StartsWith("正在检测", StringComparison.Ordinal)) StatusText = string.Empty;
                AppLog.Info($"方向检测：{Path.GetFileName(_pdfPath)} 已是正确朝向，未改动");
                return;
            }

            await Task.Run(() =>
            {
                EnsurePdfReadable();
                PdfPageRotator.RotateAll(_pdfPath, steps);
            });
            if (IsCancelled) return; // the file is fixed; nothing is on screen any more
            RememberFileStamp(force: true);

            AppLog.Info($"已自动摆正：{Path.GetFileName(_pdfPath)}，顺时针 {steps * 90}°，共 {PageCount} 页");

            // Every /Rotate changed but the content streams did not — only the rotation metadata
            // is stale in the caches, yet page bytes differ per (page, dpi) key, so drop it all.
            _renderedPages.Clear();
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
            _isOrienting = false;
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
            byte[] bytes = await GetPageBytesAsync(pageIndex, PreviewDpi);
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

    /// <summary>Rendered bytes are cached per (page, dpi) — a page the user already visited never re-renders.</summary>
    private async Task<byte[]> GetPageBytesAsync(int pageIndex, int dpi)
    {
        var key = (pageIndex, dpi);
        if (_renderedPages.TryGetValue(key, out byte[]? cached)) return cached;

        byte[] bytes;
        try
        {
            bytes = await _renderer.RenderPageAsync(_pdfPath, pageIndex, dpi);
        }
        catch (Exception) when (!File.Exists(_pdfPath))
        {
            // The user renamed/moved the file while the strip was open — track it down before
            // giving up; a plain failure here is what made the strip feel broken.
            if (!await Task.Run(TryRelocatePdf)) throw;
            bytes = await _renderer.RenderPageAsync(_pdfPath, pageIndex, dpi);
        }
        RememberFileStamp();
        _renderedPages[key] = bytes;
        return bytes;
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

    /// <summary>Edit guard: resolves a renamed file first, then fails with a message the user
    /// can act on instead of a raw path error.</summary>
    private void EnsurePdfReadable()
    {
        if (!TryRelocatePdf())
            throw new FileNotFoundException("文件已被移动或重命名，且无法自动找回。", _pdfPath);
        RememberFileStamp();
    }

    /// <summary>Drops every cached render of a page — used after its bytes changed on disk (rotation).</summary>
    private void InvalidatePage(int pageIndex)
    {
        foreach ((int page, int dpi) in _renderedPages.Keys.Where(key => key.Page == pageIndex).ToList())
            _renderedPages.Remove((page, dpi));
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
    /// Pre-scales a full render down to <see cref="ThumbMaxSide"/> with a quality resampler, so
    /// WPF only does the last ~2× of the shrink onto the tile. Sources already at or below the
    /// cap (tests feed tiny images) pass through untouched.
    /// </summary>
    private static byte[] MakeThumb(byte[] png)
    {
        using var source = SKBitmap.Decode(png);
        if (source is null) return png;
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= ThumbMaxSide) return png;

        double factor = ThumbMaxSide / (double)longest;
        int w = Math.Max(1, (int)Math.Round(source.Width * factor));
        int h = Math.Max(1, (int)Math.Round(source.Height * factor));
        using var target = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        if (!source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell))) return png;
        using var image = SKImage.FromBitmap(target);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private bool IsCancelled => _cancellationToken.IsCancellationRequested;

    private void RaisePagingChanged()
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }
}
