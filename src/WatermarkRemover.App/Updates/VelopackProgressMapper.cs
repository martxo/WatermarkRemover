using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Maps Velopack's percent-only download progress onto the shared <see cref="UpdateProgress"/>
/// record the progress window renders. A delta download spans 0–70 (70–100 is the local delta
/// application, shown as the indeterminate verifying stage); a full-package download spans the
/// whole range. Byte counts are estimated from the package size so the detail line stays honest;
/// speed is genuinely unknown and simply drops out of the display.
/// </summary>
public static class VelopackProgressMapper
{
    /// <summary>Percent of Velopack's delta progress that belongs to the download phase.</summary>
    public const int DeltaDownloadEndPercent = 70;

    public static UpdateProgress Map(int percent, bool isDelta, long? packageBytes, string? sourceDescription)
    {
        percent = Math.Clamp(percent, 0, 100);

        if (isDelta && percent >= DeltaDownloadEndPercent)
        {
            // Delta application runs locally: indeterminate bar, no byte detail.
            return new UpdateProgress(
                UpdateStage.Verifying, 100, packageBytes ?? 0, packageBytes, null, sourceDescription);
        }

        double fraction = isDelta
            ? percent / (double)DeltaDownloadEndPercent
            : percent / 100.0;
        long? bytesReceived = packageBytes is > 0 ? (long)(packageBytes.Value * fraction) : null;

        return new UpdateProgress(
            UpdateStage.Downloading,
            fraction * 100.0,
            bytesReceived ?? 0,
            packageBytes,
            null,
            sourceDescription);
    }
}
