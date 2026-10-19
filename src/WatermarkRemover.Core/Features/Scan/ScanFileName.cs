namespace WatermarkRemover.Core.Features.Scan;

/// <summary>
/// Automatic file name for scan outputs: 「扫描yyyyMMdd-HHmmss.pdf」. Collisions are resolved by
/// the shared <see cref="OutputNameGenerator.FindAvailablePath"/> counter, never by overwriting.
/// </summary>
public static class ScanFileName
{
    /// <summary>Returns the base file name (with extension) for the given scan time.</summary>
    public static string Create(DateTime timestamp) => $"扫描{timestamp:yyyyMMdd-HHmmss}.pdf";
}
