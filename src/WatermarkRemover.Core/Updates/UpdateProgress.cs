namespace WatermarkRemover.Core.Updates;

/// <summary>Phase of the update flow, so the UI can show a friendly stage label.</summary>
public enum UpdateStage
{
    /// <summary>Querying the release channel.</summary>
    Checking = 0,

    /// <summary>Downloading the update package from the official link or a mirror.</summary>
    Downloading = 1,

    /// <summary>SHA256 integrity verification of the downloaded package.</summary>
    Verifying = 2,

    /// <summary>Package ready; the app is about to swap files and restart.</summary>
    ReadyToRestart = 3,
}

/// <summary>
/// One progress snapshot of the update download. <see cref="Percent"/> is null when the total
/// size is unknown (no Content-Length); <see cref="SourceDescription"/> names the download
/// channel in use ("官方下载", "备用通道 ghfast.top", …) so a mirror fallback is visible.
/// </summary>
public sealed record UpdateProgress(
    UpdateStage Stage,
    double? Percent,
    long BytesReceived,
    long? TotalBytes,
    double? SpeedBytesPerSecond,
    string? SourceDescription);

/// <summary>
/// Pure formatting helpers for update progress text. Kept free of UI types so the numbers a user
/// sees during a download ("已下载 45.2 MB / 122.0 MB · 3.1 MB/s · 约需 25 秒") are unit-tested.
/// All sections degrade gracefully: an unknown total or a weak speed simply drops out.
/// </summary>
public static class ProgressTextFormatter
{
    /// <summary>Speeds below 1 KB/s are too noisy to be useful — drop the speed/ETA instead.</summary>
    public const double MinSpeedBytesPerSecond = 1024;

    /// <summary>Human byte size, one decimal above bytes: "812 B", "45.2 KB", "122.0 MB".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return FormattableString.Invariant($"{bytes} B");
        if (bytes < 1024 * 1024) return FormattableString.Invariant($"{bytes / 1024.0:F1} KB");
        if (bytes < 1024L * 1024 * 1024)
            return FormattableString.Invariant($"{bytes / (1024.0 * 1024):F1} MB");
        return FormattableString.Invariant($"{bytes / (1024.0 * 1024 * 1024):F1} GB");
    }

    /// <summary>Transfer speed: "512 B/s", "3.1 MB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond) =>
        FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";

    /// <summary>
    /// Remaining-time hint ("约需 25 秒" / "约需 3 分钟" / "约需 2 小时"), or null when it cannot
    /// be estimated honestly (unknown total, stalled or too-slow connection).
    /// </summary>
    public static string? TryFormatEta(long bytesReceived, long? totalBytes, double? speedBytesPerSecond)
    {
        if (totalBytes is not > 0 || speedBytesPerSecond is not >= MinSpeedBytesPerSecond)
            return null;

        long remaining = totalBytes.Value - bytesReceived;
        if (remaining <= 0) return null;

        double seconds = remaining / speedBytesPerSecond.Value;
        if (seconds <= 90)
            return FormattableString.Invariant($"约需 {(long)Math.Ceiling(seconds)} 秒");
        if (seconds <= 90 * 60)
            return FormattableString.Invariant($"约需 {(long)Math.Ceiling(seconds / 60)} 分钟");
        return FormattableString.Invariant($"约需 {(long)Math.Ceiling(seconds / 3600)} 小时");
    }

    /// <summary>
    /// The download detail line: "已下载 45.2 MB / 122.0 MB · 3.1 MB/s · 约需 25 秒". Sections
    /// after the byte count are omitted when the data does not support them.
    /// </summary>
    public static string FormatDownloadDetail(long bytesReceived, long? totalBytes, double? speedBytesPerSecond)
    {
        string text = totalBytes is > 0
            ? $"已下载 {FormatBytes(bytesReceived)} / {FormatBytes(totalBytes.Value)}"
            : $"已下载 {FormatBytes(bytesReceived)}";

        if (speedBytesPerSecond is >= MinSpeedBytesPerSecond)
            text += " · " + FormatSpeed(speedBytesPerSecond.Value);

        string? eta = TryFormatEta(bytesReceived, totalBytes, speedBytesPerSecond);
        if (eta is not null)
            text += " · " + eta;

        return text;
    }
}
