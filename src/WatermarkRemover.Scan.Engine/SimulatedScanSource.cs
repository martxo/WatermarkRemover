using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Hardware-free scan source used by the automated checks. It replays image files dropped into a
/// folder, or synthesises deterministic document pages when the folder is empty, so the whole
/// pipeline (colour / background / blank-page handling, OCR, naming, output) can be exercised
/// without a scanner being present.
/// </summary>
public sealed class SimulatedScanSource : IScanSource
{
    /// <summary>Image extensions replayed from the simulation folder, in file-name order.</summary>
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    private readonly string _directory;
    private readonly int _syntheticPageCount;
    private readonly Action<string, Exception?>? _trace;

    /// <param name="directory">
    /// Folder replayed by this source. Defaults to <c>IPSCLONE_SIM_DIR</c>, then to
    /// <c>&lt;current directory&gt;\sim-pages</c>.
    /// </param>
    /// <param name="syntheticPageCount">Page count used when the folder has no images (default 2).</param>
    /// <param name="trace">Optional diagnostic sink.</param>
    public SimulatedScanSource(string? directory = null, int syntheticPageCount = 2, Action<string, Exception?>? trace = null)
    {
        _directory = ResolveDirectory(directory);
        _syntheticPageCount = syntheticPageCount > 0 ? syntheticPageCount : 2;
        _trace = trace;
    }

    public string Id => "simulated";

    public string DisplayName => "模拟扫描仪（无硬件验证用）";

    /// <summary>The folder this source replays (or synthesises into, conceptually).</summary>
    public string SimulationDirectory => _directory;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<IReadOnlyList<ScanDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory"] = _directory,
            ["ImageCount"] = FindImages().Count.ToString(),
            ["SyntheticPageCount"] = _syntheticPageCount.ToString(),
        };

        var device = new ScanDeviceInfo
        {
            SourceId = Id,
            DeviceId = "simulated:default",
            Name = "Simulated Device",
            Manufacturer = "IpsClone (simulated)",
            Capabilities = capabilities,
        };

        return Task.FromResult<IReadOnlyList<ScanDeviceInfo>>(new[] { device });
    }

    public Task<IScanSession> OpenAsync(
        ScanDeviceInfo device,
        ScanSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);

        var session = new SimulatedScanSession(device, settings, FindImages(), _syntheticPageCount, _trace);
        return Task.FromResult<IScanSession>(session);
    }

    private static string ResolveDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("IPSCLONE_SIM_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return Path.Combine(System.IO.Directory.GetCurrentDirectory(), "sim-pages");
    }

    /// <summary>Returns the readable image files in the simulation folder, sorted by name. Never throws.</summary>
    private List<string> FindImages()
    {
        var result = new List<string>();
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return result;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(_directory))
            {
                if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(file);
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"读取模拟图片目录「{_directory}」失败，将改用合成页面。", ex);
            result.Clear();
        }

        return result;
    }
}
