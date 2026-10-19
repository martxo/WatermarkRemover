using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Page source for <see cref="SimulatedScanSource"/>. Pages come from image files when the folder has
/// them, otherwise from <see cref="SimulatedPageFactory"/>. Failures degrade to synthetic pages so
/// the session never throws and never leaves the caller without pixels.
/// </summary>
internal sealed class SimulatedScanSession : IScanSession
{
    private readonly ScanSettings _settings;
    private readonly IReadOnlyList<string> _files;
    private readonly int _syntheticPageCount;
    private readonly Action<string, Exception?>? _trace;
    private int _index;

    public SimulatedScanSession(
        ScanDeviceInfo device,
        ScanSettings settings,
        IReadOnlyList<string> files,
        int syntheticPageCount,
        Action<string, Exception?>? trace)
    {
        Device = device;
        _settings = settings;
        _files = files;
        _syntheticPageCount = syntheticPageCount;
        _trace = trace;
    }

    public ScanDeviceInfo Device { get; }

    public Task<RasterImage?> ReadPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Continuous scan off means "one page only", mirroring a single flatbed capture.
        if (!_settings.IsFlatbed && !_settings.ContinuousScan && _index >= 1)
        {
            return Task.FromResult<RasterImage?>(null);
        }

        var total = _files.Count > 0 ? _files.Count : _syntheticPageCount;
        if (_index >= total)
        {
            return Task.FromResult<RasterImage?>(null);
        }

        RasterImage image;
        if (_files.Count > 0)
        {
            var file = _files[_index];
            try
            {
                image = SkiaRaster.DecodeFile(file);
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"解码模拟图片「{file}」失败，改用合成页面。", ex);
                image = SimulatedPageFactory.Create(_index);
            }
        }
        else
        {
            image = SimulatedPageFactory.Create(_index);
        }

        _index++;
        return Task.FromResult<RasterImage?>(image);
    }

    public ValueTask DisposeAsync()
    {
        // Nothing to release: pages are decoded lazily, one per call, with no shared state.
        return ValueTask.CompletedTask;
    }
}
