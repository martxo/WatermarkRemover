using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// A live WIA session over one item. Page pulling is identical for the flatbed (a single page) and
/// the feeder (many pages): <see cref="ReadPageAsync"/> returns null as soon as the device reports
/// that there is nothing more to scan.
/// </summary>
internal sealed class WiaScanSession : IScanSession
{
    private readonly ScanSettings _settings;
    private readonly Action<string, Exception?>? _trace;
    private readonly List<string> _notes = new();

    private object? _device;
    private object? _item;
    private bool _hasReadPage;
    private bool _exhausted;
    private bool _disposed;

    // The paper source actually in effect. This starts as the requested feed mode and can change once,
    // when an automatic run fails to pull from the feeder and falls back to the glass.
    private FeedMode _effectiveSource;

    public WiaScanSession(
        object device,
        object item,
        ScanDeviceInfo deviceInfo,
        ScanSettings settings,
        Action<string, Exception?>? trace)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        Device = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _trace = trace;
        _effectiveSource = _settings.Feed;

        ApplySettings();
    }

    public ScanDeviceInfo Device { get; }

    /// <summary>Per-property results, used by <see cref="WiaScanSource.DescribeCapabilities"/>.</summary>
    public IReadOnlyList<string> DiagnosticNotes => _notes;

    /// <summary>
    /// Whether the glass is being used. Follows <see cref="ResolveDocumentHandling"/> so the page loop
    /// always agrees with what was programmed into the device - including after an automatic run has
    /// fallen back from the feeder to the glass.
    /// </summary>
    private bool UsesFlatbed => _effectiveSource == FeedMode.Flatbed;

    public Task<RasterImage?> ReadPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed || _exhausted)
        {
            return Task.FromResult<RasterImage?>(null);
        }

        var flatbed = UsesFlatbed;

        // The flatbed yields exactly one page; a feeder stops early when the caller asked for one page.
        if (flatbed && _hasReadPage)
        {
            return Task.FromResult<RasterImage?>(null);
        }

        if (!flatbed && !_settings.ContinuousScan && _hasReadPage)
        {
            return Task.FromResult<RasterImage?>(null);
        }

        // On the feeder, an empty FEED_READY flag after the first page is the normal "batch done" signal.
        if (!flatbed && _hasReadPage && !FeederHasPaper())
        {
            _exhausted = true;
            return Task.FromResult<RasterImage?>(null);
        }

        byte[] bytes;
        try
        {
            bytes = TransferPage();
        }
        catch (Exception ex)
        {
            // An automatic run cannot know in advance which source holds the original, so the first
            // failure is the signal to try the glass.
            if (!_hasReadPage && TryFallbackToFlatbed())
            {
                bytes = TransferPage();
                _hasReadPage = true;
                return Task.FromResult<RasterImage?>(SkiaRaster.Decode(bytes));
            }

            _exhausted = true;
            if (!_hasReadPage)
            {
                // Nothing was produced at all: surface the full exception (including the COM error).
                throw new InvalidOperationException("WIA 取像失败：未取得图像数据。", ex);
            }

            _trace?.Invoke("WIA 进纸器已无更多纸张（Transfer 失败，视为扫描结束）。", ex);
            return Task.FromResult<RasterImage?>(null);
        }

        _hasReadPage = true;
        return Task.FromResult<RasterImage?>(SkiaRaster.Decode(bytes));
    }

    /// <summary>
    /// Repoints an automatic run at the glass after the feeder failed to deliver the first page. Only
    /// the first page may fall back: a later failure means the feeder ran dry, not that the wrong
    /// source was chosen, and re-reading the glass there would duplicate the document.
    /// </summary>
    /// <returns><c>true</c> when the device was switched and the caller should retry the transfer.</returns>
    private bool TryFallbackToFlatbed()
    {
        if (_effectiveSource != FeedMode.Auto)
        {
            return false;
        }

        var device = _device;
        if (device is null)
        {
            return false;
        }

        _effectiveSource = FeedMode.Flatbed;
        _notes.Add("自动进纸：送纸器取像失败，已改用平板重试。");
        _trace?.Invoke("自动进纸：送纸器取像失败，改用平板重试。", null);
        WiaInterop.TrySetProperty(
            device,
            WiaInterop.DpsDocumentHandlingSelect,
            WiaInterop.HandlingFlatbed,
            _trace,
            _notes);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WiaInterop.TryRelease(ref _item);
        WiaInterop.TryRelease(ref _device);
    }

    /// <summary>Pushes the requested settings onto the WIA item; each property is set independently.</summary>
    private void ApplySettings()
    {
        var item = _item;
        if (item is null)
        {
            return;
        }

        var dpi = (int)_settings.Resolution;

        // --- resolution ---------------------------------------------------
        WiaInterop.TrySetProperty(item, WiaInterop.IpsXRes, dpi, _trace, _notes);
        WiaInterop.TrySetProperty(item, WiaInterop.IpsYRes, dpi, _trace, _notes);

        // --- colour intent -------------------------------------------------
        var intent = ResolveIntent(_settings.Color, out var intentNote);
        if (intentNote is not null)
        {
            _notes.Add(intentNote);
        }

        if (intent is not null)
        {
            WiaInterop.TrySetProperty(item, WiaInterop.IpsCurIntent, intent.Value, _trace, _notes);
        }

        // --- pixel data type -----------------------------------------------
        var dataType = ResolveDataType(_settings.Color);
        if (dataType is not null)
        {
            WiaInterop.TrySetProperty(item, WiaInterop.IpaDataType, dataType.Value, _trace, _notes);
        }
        else
        {
            _notes.Add("色彩模式为自动：WIA_IPA_DATATYPE 交由驱动决定，未强制设置。");
        }

        // --- scan area ------------------------------------------------------
        if (TryResolveExtents(dpi, out var widthPx, out var heightPx, out var extentNote))
        {
            WiaInterop.TrySetProperty(item, WiaInterop.IpsXExtent, widthPx, _trace, _notes);
            WiaInterop.TrySetProperty(item, WiaInterop.IpsYExtent, heightPx, _trace, _notes);
        }

        if (extentNote is not null)
        {
            _notes.Add(extentNote);
        }

        // --- origin ----------------------------------------------------------
        WiaInterop.TrySetProperty(item, WiaInterop.IpsXPos, 0, _trace, _notes);
        WiaInterop.TrySetProperty(item, WiaInterop.IpsYPos, 0, _trace, _notes);

        // --- paper source / duplex -------------------------------------------
        // WIA_DPS_DOCUMENT_HANDLING_SELECT belongs to the DEVICE ROOT item, not the scan item: setting
        // it on the scan item fails with "无法找到 ID" on the Brother DCP-B7658DW.
        var select = ResolveDocumentHandling(_effectiveSource, out var sourceNote);
        if (sourceNote is not null)
        {
            _notes.Add(sourceNote);
        }

        var device = _device;
        if (device is not null)
        {
            WiaInterop.TrySetProperty(device, WiaInterop.DpsDocumentHandlingSelect, select, _trace, _notes);
        }

        if (UsesFlatbed && _settings.Duplex != DuplexMode.Off)
        {
            _notes.Add("平板进纸不支持双面扫描，已忽略 Duplex 设置。");
        }
    }

    /// <summary>Maps the colour mode onto the documented WIA_IPS_CUR_INTENT bits.</summary>
    private static int? ResolveIntent(ColorMode color, out string? note)
    {
        note = null;
        switch (color)
        {
            case ColorMode.Color24:
                return WiaInterop.IntentColor;
            case ColorMode.TrueGray:
            case ColorMode.GrayDiffusion:
                return WiaInterop.IntentGrayScale;
            case ColorMode.BlackWhite:
                return WiaInterop.IntentText;
            default:
                note = "色彩模式为自动：未设置 WIA_IPS_CUR_INTENT，由驱动自行决定。";
                return null;
        }
    }

    /// <summary>Maps the colour mode onto WIA_IPA_DATATYPE; null means "let the driver decide".</summary>
    private static int? ResolveDataType(ColorMode color) => color switch
    {
        ColorMode.Color24 => WiaInterop.DataColor,        // WIA_DATA_COLOR (3)
        ColorMode.GrayDiffusion => WiaInterop.DataDither, // WIA_DATA_DITHER (1)
        ColorMode.TrueGray => WiaInterop.DataGrayScale,   // WIA_DATA_GRAYSCALE (2)
        ColorMode.BlackWhite => WiaInterop.DataThreshold, // WIA_DATA_THRESHOLD (0)
        _ => null,
    };

    /// <summary>Builds the WIA_DPS_DOCUMENT_HANDLING_SELECT bit mask. DUPLEX requires the feeder.</summary>
    /// <remarks>
    /// There is no WIA "automatic feed" bit: the device only understands FEEDER and FLATBED. The target
    /// resolves "auto" by whether the feeder holds paper, but the DCP-B7658DW reports
    /// WIA_DPS_DOCUMENT_HANDLING_STATUS (3087) as a constant, both with and without paper in the feeder,
    /// so that probe is unavailable here. Auto therefore means "prefer the feeder and fall back to the
    /// glass if the first page cannot be pulled" - see <see cref="TryFallbackToFlatbed"/>.
    /// </remarks>
    private int ResolveDocumentHandling(FeedMode source, out string? note)
    {
        note = null;

        var select = source switch
        {
            FeedMode.Flatbed => WiaInterop.HandlingFlatbed,
            FeedMode.Adf => WiaInterop.HandlingFeeder,
            _ => WiaInterop.HandlingFeeder,
        };

        if (source == FeedMode.Auto)
        {
            note = "进纸方式为自动：设备无法报告送纸器是否有纸，已优先按送纸器取像；若首张取像失败会自动改用平板。";
        }

        var usesFeeder = (select & WiaInterop.HandlingFeeder) != 0;
        if (usesFeeder && _settings.Duplex != DuplexMode.Off)
        {
            select |= WiaInterop.HandlingDuplex;
        }

        return select;
    }

    /// <summary>Converts the requested paper size into WIA pixel extents.</summary>
    private bool TryResolveExtents(int dpi, out int widthPx, out int heightPx, out string? note)
    {
        widthPx = 0;
        heightPx = 0;
        note = null;

        if (WiaPaper.TryGetInches(_settings.Paper, out var widthInches, out var heightInches))
        {
            widthPx = (int)Math.Round(widthInches * dpi);
            heightPx = (int)Math.Round(heightInches * dpi);
            note = $"扫描区域：{_settings.Paper} @ {dpi}dpi → {widthPx}×{heightPx} 像素";
            return true;
        }

        if (_settings.CustomWidthPx is > 0 && _settings.CustomHeightPx is > 0)
        {
            widthPx = _settings.CustomWidthPx.Value;
            heightPx = _settings.CustomHeightPx.Value;
            note = $"扫描区域：使用自定义尺寸 {widthPx}×{heightPx} 像素";
            return true;
        }

        note = "纸张尺寸为自动且未给出自定义像素尺寸：未设置 XEXTENT/YEXTENT，交由驱动决定。";
        return false;
    }

    /// <summary>
    /// Runs <c>IItem::Transfer</c> and returns the encoded image bytes.
    /// </summary>
    /// <remarks>
    /// PNG is requested, but drivers may answer with their own native format - the Brother DCP-B7658DW
    /// returns an uncompressed BMP - so the payload is decoded by content, never by the requested guid.
    /// </remarks>
    private byte[] TransferPage()
    {
        var item = _item ?? throw new InvalidOperationException("WIA 会话已释放。");
        return WiaInterop.TransferImageBytes(item, WiaInterop.FormatPng);
    }

    /// <summary>
    /// True when the feeder reports FEED_READY. The status lives on the device root item, not the scan
    /// item, and some drivers (the DCP-B7658DW included) report a constant value, so an exhausted batch
    /// ultimately relies on the Transfer failure fallback in <see cref="ReadPageAsync"/>.
    /// </summary>
    private bool FeederHasPaper()
    {
        var device = _device;
        if (device is null)
        {
            return false;
        }

        var status = WiaInterop.TryReadPropertyInt(device, WiaInterop.DpsDocumentHandlingStatus);
        if (status < 0)
        {
            _trace?.Invoke("无法读取进纸状态，按仍有纸张继续。", null);
            return true;
        }

        return (status & WiaInterop.StatusFeedReady) != 0;
    }
}
