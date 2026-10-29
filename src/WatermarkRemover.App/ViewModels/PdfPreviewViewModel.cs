﻿using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Preview;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Pdf;
using WatermarkRemover.Pdf.Preview;

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
public sealed partial class PdfPreviewViewModel : ObservableObject
{
    /// <summary>
    /// Screen rendering DPI: an A4 page lands at roughly 1240×1754 — crisp on a monitor and far
    /// below the renderer's 2400 px long-side cap, so no downscaling cost.
    /// </summary>
    private const int PreviewDpi = 150;

    /// <summary>
    /// Thumbnail rendering DPI. 96 is ample for the 92–368 px tiles and keeps a long strip's
    /// fill cheap; the canvas still renders separately at <see cref="PreviewDpi"/>.
    /// </summary>
    private const int ThumbnailDpi = 96;

    /// <summary>
    /// Render DPI for orientation probes only. Direction voting reads line-box layout, not
    /// small glyphs, so a 90dpi raster halves the pixels the OCR pays for with no accuracy
    /// loss. Tiles and canvas stay on 150 — this is a separate cache key and never displayed.
    /// </summary>
    private const int OrientationDpi = 90;

    /// <summary>
    /// Longest side a thumbnail is pre-scaled to. 480 keeps ~1.3× oversample at the 4.0 zoom
    /// ceiling (368 px tile) while cutting the decoded tile memory by roughly half versus 720.
    /// The big downscale from the page render happens here with a quality kernel, not in WPF.
    /// </summary>
    private const int ThumbMaxSide = 480;

    /// <summary>Base tile size at scale 1.0; the grid's zoom buttons multiply this.</summary>
    public const double BaseTileWidth = 92.0;
    public const double BaseTileHeight = 118.0;
    public const double MinThumbScale = 0.6;
    public const double MaxThumbScale = 4.0;
    private const double ThumbScaleStep = 0.15;

    private readonly PdfDocumentFile _file;
    private readonly IPdfPageRenderer _renderer;
    private readonly PdfPageRenderCache _cache;
    private readonly PdfPageEditService _edits;
    private readonly PdfScanPostProcessor _postProcessor;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Host-supplied confirmation for the destructive page delete; null = allowed (tests).</summary>
    private readonly Func<int, bool>? _confirmPageDelete;

    /// <summary>Notified when the PDF was found under a new name after the user renamed it;
    /// the host keeps the row's output path in step. Null = nobody cares (tests).</summary>
    private readonly Action<string>? _onRelocated;

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

    /// <summary>Bumped whenever the tile fill is released/restarted; an older fill stops early so
    /// a folded strip cannot keep decoding images in the background.</summary>
    private int _thumbnailLoadGeneration;

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

    /// <summary>Pages that were already on screen while the scan ran. Applied once when the tile
    /// row is first built, so the strip opens with content instead of an empty grid; later
    /// rebuilds (rotate / delete / insert) start clean and render from the PDF.</summary>
    private IReadOnlyList<ImageSource>? _seedThumbnails;

    public PdfPreviewViewModel(
        string pdfPath, string displayName, IPdfPageRenderer renderer,
        CancellationToken cancellationToken = default,
        Func<int, bool>? confirmPageDelete = null,
        IOcrEngine? ocrEngine = null, bool autoOrient = false,
        Action<string>? onRelocated = null, bool autoCleanBlank = false,
        Func<IProgress<string>, Task<string?>>? scanProvider = null,
        IReadOnlyList<ImageSource>? seedThumbnails = null)
    {
        _renderer = renderer;
        _cancellationToken = cancellationToken;
        _confirmPageDelete = confirmPageDelete;
        _ocrEngine = ocrEngine;
        _autoOrient = autoOrient;
        _onRelocated = onRelocated;
        _autoCleanBlank = autoCleanBlank;
        _scanProvider = scanProvider;
        _seedThumbnails = seedThumbnails;

        _file = new PdfDocumentFile(
            pdfPath,
            pathChanged: OnPdfPathChanged,
            debug: AppLog.Debug,
            info: AppLog.Info);
        _cache = new PdfPageRenderCache(
            renderer,
            _file,
            cancellationToken,
            debug: AppLog.Debug,
            warn: message => AppLog.Warn(message));
        _postProcessor = new PdfScanPostProcessor(
            _cache,
            _file,
            ocrEngine,
            cancellationToken,
            debug: AppLog.Debug,
            info: AppLog.Info);
        _edits = new PdfPageEditService(_cache, _file);

        Title = displayName;
        _transientStatus = new TransientStatus(
            read: () => StatusText,
            write: text => StatusText = text ?? string.Empty);
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
            () => _ = AutoOrientAsync(reportFailure: true),
            () => _ocrEngine is not null && !IsOrienting && !IsRotating && PageCount > 0);
        // Any document with at least one page accepts a scan at any slot — front, middle or
        // end. A one-page scan is exactly the case that needs the bookends most (the missing
        // page is usually the one before or after), so PageCount > 0, never > 1.
        InsertScanAfterCommand = new RelayCommand<int>(
            index => _ = InsertScanAfterAsync(index),
            _ => _scanProvider is not null && !IsRotating && !IsOrienting && PageCount > 0);
        // The "append after the last page" entry lives OUTSIDE the wrap flow (the strip's zoom
        // rail). As a trailing plus slot inside the flow it widened only the last unit by 14px;
        // a row with room for a tile but not for tile+14 bumped the whole last page to the next
        // row and left a cell-sized hole (2026-10-22). Out here it costs the flow nothing.
        AppendScanCommand = new RelayCommand(
            () => _ = InsertScanAfterAsync(PageCount - 1),
            () => _scanProvider is not null && !IsRotating && !IsOrienting && PageCount > 0);
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

    /// <summary>Scans one pass and appends it after the last page — the end-of-document slot
    /// that used to be a trailing plus inside the strip. It is a rail button, outside the wrap
    /// flow, so the last page's cell is exactly as wide as every other page's cell and rows
    /// always pack tight (the widened last cell was the "hole" bug of 2026-10-22).</summary>
    public RelayCommand AppendScanCommand { get; }

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

    /// <summary>Transient confirmations (rotate/delete/straighten) share the app's one expiry rule.</summary>
    private readonly TransientStatus _transientStatus;

    /// <summary>
    /// Shows a confirmation for a couple of seconds, then clears it. Rotating, deleting and
    /// straightening all save straight into the PDF, and the tile repaint alone is too quiet to
    /// read as "it worked" — but a line that never goes away makes the next real error easy to
    /// miss, so done-messages expire.
    /// </summary>
    private void ShowTransientStatus(string message) => _transientStatus.Show(message);

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Reads the page count, then renders page 1. Failures surface in <see cref="StatusText"/>.</summary>
    public async Task LoadAsync()
    {
        if (IsCancelled) return;

        // A scan can be long; the slow-operation warning would fire on every healthy load, so the
        // per-step timings below carry the useful numbers instead.
        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "打开预览", Path.GetFileName(_file.Path), slowMs: 0);

        try
        {
            PageCount = await _renderer.GetPageCountAsync(_file.Path);
        }
        catch (OperationCanceledException)
        {
            op.Complete("已取消");
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开这份 PDF：{ex.Message}";
            op.Fail($"无法读取页数：{ex.Message}", ex);
            return;
        }

        op.Step("读取页数", $"{PageCount} 页");

        if (IsCancelled)
        {
            op.Complete("已取消");
            return;
        }

        if (PageCount == 0)
        {
            StatusText = "这份 PDF 没有可显示的页面。";
            op.Complete("没有可显示的页面");
            return;
        }

        BuildThumbnails();
        await RenderAsync(0);
        op.Step("首屏渲染", $"第 1 页，{PreviewDpi}dpi");

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
        StartThumbnailLoad();
        op.Complete($"{PageCount} 页，首屏已渲染");
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
    /// Renders one page and shows it. Revisiting an already rendered page skips the renderer
    /// entirely; a newer request always wins, so rapid paging cannot show a stale page.
    /// </summary>
    private async Task RenderAsync(int pageIndex, bool force = false)
    {
        if (pageIndex < 0 || pageIndex >= _pageCount) return;
        if (!force && pageIndex == _displayedPageIndex && CurrentImage is not null) return;

        long startTicks = Stopwatch.GetTimestamp();
        int generation = ++_loadGeneration;
        PageIndex = pageIndex;
        IsLoading = true;
        StatusText = $"正在渲染第 {pageIndex + 1} 页…";

        try
        {
            byte[] bytes = await _cache.GetPageBytesAsync(pageIndex, PreviewDpi, priority: true);
            if (IsCancelled || generation != _loadGeneration) return;

            CurrentImage = Decode(bytes);
            _displayedPageIndex = pageIndex;
            StatusText = string.Empty;
            AppLog.Debug($"预览渲染完成：第 {pageIndex + 1} 页，{PreviewDpi}dpi，" +
                         $"{(long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds} ms");
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
            AppLog.Warn($"预览渲染失败：第 {pageIndex + 1} 页，{PreviewDpi}dpi，" +
                        $"{(long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds} ms", ex);
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    /// <summary>Points the strip at the fallback copy written when the original was locked.</summary>
    internal void RelocateTo(string newPath) => _file.RelocateTo(newPath);

    /// <summary>One place where a rename or locked-copy fallback updates the header and host.</summary>
    private void OnPdfPathChanged(string path)
    {
        Title = Path.GetFileName(path);
        OnPropertyChanged(nameof(Title));
        _onRelocated?.Invoke(path);
    }

    /// <summary>
    /// Drops this strip's rendered-page byte cache. The bytes are only a speed cache — a miss
    /// re-renders the page on demand — while each full-page PNG is multi-megabyte LOH. The host
    /// calls this for strips that are folded or off screen, so a session that previewed many
    /// documents no longer keeps every page of every one of them resident.
    /// </summary>
    public void TrimRenderCache() => _cache.Trim();

    /// <summary>Whether any tile currently holds a decoded bitmap.</summary>
    public bool HasThumbnailImages => Thumbnails.Any(t => t.Image is not null);

    /// <summary>
    /// Drops every decoded tile bitmap (and the full-page byte cache). Called when a strip folds
    /// or another row takes the preview, so a long session keeps one strip's pixels, not all of
    /// them. Unfolding calls <see cref="ReloadThumbnailImages"/>.
    /// </summary>
    public void ReleaseThumbnailImages()
    {
        _thumbnailLoadGeneration++;
        int released = Thumbnails.Count(thumb => thumb.Image is not null);
        foreach (var thumb in Thumbnails)
            thumb.ClearImage();
        long cacheBytes = _cache.CacheBytes;
        TrimRenderCache();
        AppLog.Debug($"预览已释放缩略图：{released} 张，渲染缓存 {cacheBytes} 字节");
        // The canvas bitmap is multi-megabyte and no row surface shows it while folded; without
        // this, 70 scanned documents would keep 70 decoded 150-dpi pages resident.
        CurrentImage = null;
    }

    /// <summary>Rebuilds the tile bitmaps after a folded strip is unfolded again.</summary>
    public void ReloadThumbnailImages()
    {
        if (Thumbnails.Count == 0) return;

        _thumbnailLoadGeneration++;
        _ = LoadThumbnailsAsync(Thumbnails.ToList(), _thumbnailLoadGeneration);
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
