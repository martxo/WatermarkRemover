using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Serialises every scanner session — the hardware is exclusive, and the scan page's button and
/// the preview strips' gap buttons must never drive it at the same time. Owns the busy/status
/// transitions those two entries share, so they cannot drift apart. Callers keep the surrounding
/// orchestration: filing the PDF into the job list and background naming stay in the host.
/// </summary>
public sealed class ScanCoordinator
{
    private readonly ScanService _scanService;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<bool> _setBusy;
    private readonly Action<string> _setStatus;
    private CancellationTokenSource? _cts;

    public ScanCoordinator(AppSettings settings, Action<bool> setBusy, Action<string> setStatus)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _scanService = new ScanService(settings);
    }

    /// <summary>
    /// Runs one scan with the layout/colour chosen on the scan page, mirroring progress into the
    /// status line and (when given) the caller's sink. Throws <see cref="ScannerBusyException"/>
    /// when another scan still holds the hardware.
    /// </summary>
    public async Task<ScanOutcome> ScanOnceAsync(
        IProgress<string>? progressSink,
        IProgress<ScannedPageInfo>? pageSink = null)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(true))
            throw new ScannerBusyException();

        using var cts = new CancellationTokenSource();
        _cts = cts;
        _setBusy(true);
        _setStatus("正在准备扫描…");
        var progress = new Progress<string>(message =>
        {
            _setStatus(message);
            progressSink?.Report(message);
        });
        try
        {
            var (duplex, color) = ScanModes();
            return await _scanService.ScanAsync(duplex, color, progress, cts.Token, pageSink)
                .ConfigureAwait(true);
        }
        finally
        {
            _cts = null;
            _gate.Release();
            _setBusy(false);
            _setStatus(string.Empty);
        }
    }

    /// <summary>
    /// Requests cancellation of the running scan. Pages already captured are still assembled into
    /// a partial PDF by <see cref="ScanService"/>; with no pages yet nothing is written.
    /// </summary>
    public void CancelScan() => _cts?.Cancel();

    /// <summary>
    /// The preview strip's gap-button scan: one pass handed back as the scanned PDF's path for the
    /// strip to splice in. Runs the exact same session as <see cref="ScanOnceAsync"/>.
    /// </summary>
    public async Task<string?> ScanIntoStripAsync(IProgress<string> progress)
    {
        ScanOutcome outcome = await ScanOnceAsync(progress).ConfigureAwait(true);
        if (!outcome.Success)
        {
            AppLog.Error($"插入扫描失败：{outcome.Error}");
            throw new InvalidOperationException(outcome.Error ?? "扫描失败");
        }
        return outcome.OutputPath;
    }

    /// <summary>The scan page's layout and colour, mapped from the settings' UI ints.</summary>
    private (DuplexMode Duplex, ColorMode Color) ScanModes()
    {
        var duplex = (DuplexMode)Math.Clamp(_settings.ScanDuplex, 0, 2);
        // UI ints (0 自动 / 1 彩色 / 2 灰度 / 3 黑白) deliberately differ from the engine enum's
        // numeric order (GrayDiffusion sits between Color24 and TrueGray) — map explicitly.
        var color = _settings.ScanColor switch
        {
            0 => ColorMode.Auto,
            1 => ColorMode.Color24,
            2 => ColorMode.TrueGray,
            _ => ColorMode.BlackWhite,
        };
        return (duplex, color);
    }
}
