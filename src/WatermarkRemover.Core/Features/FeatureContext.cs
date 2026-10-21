using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features;

/// <summary>
/// Everything a feature may read while processing one file. Capture a context when the job is
/// enqueued: it holds a deep copy of the settings, so edits made in the settings window while a
/// batch runs can never be observed mid-flight by background threads.
/// </summary>
public sealed class FeatureContext
{
    public FeatureContext(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Immutable-by-convention deep copy of the user settings.</summary>
    public AppSettings Settings { get; }

    /// <summary>Optional per-stage progress: (inputPath, stageText) raised on the worker thread
    /// while the feature runs — the host routes it to the owning row so "检测水印 / 识别文字 /
    /// 检查朝向" show up as distinct states instead of one blur.</summary>
    public Action<string, string>? Progress { get; init; }

    /// <summary>Snapshots the current settings into a context safe for background use.</summary>
    public static FeatureContext Capture(AppSettings source) => new(source.Clone());

    /// <summary>As <see cref="Capture(AppSettings)"/>, plus a stage reporter for the row.</summary>
    public static FeatureContext Capture(AppSettings source, Action<string, string>? progress) =>
        new(source.Clone()) { Progress = progress };
}
