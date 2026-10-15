namespace WatermarkRemover.Core.Features;

/// <summary>Outcome of running one feature on one file.</summary>
public enum FeatureStatus
{
    /// <summary>The feature produced an output file.</summary>
    Succeeded = 0,

    /// <summary>The file is valid but there was nothing for the feature to do.</summary>
    NoChange = 1,

    /// <summary>Processing failed; see <see cref="FeatureResult.Error"/>.</summary>
    Failed = 2,
}

/// <summary>Unified, feature-agnostic result. The queue and UI only ever see this shape.</summary>
public sealed class FeatureResult
{
    private FeatureResult(FeatureStatus status, string inputPath, string? outputPath, string? summary, int itemCount, string? error)
    {
        Status = status;
        InputPath = inputPath;
        OutputPath = outputPath;
        Summary = summary;
        ItemCount = itemCount;
        Error = error;
    }

    public FeatureStatus Status { get; }

    public string InputPath { get; }

    /// <summary>The generated file, when <see cref="FeatureStatus.Succeeded"/>.</summary>
    public string? OutputPath { get; }

    /// <summary>Short human description of what happened, e.g. "已去除 3 处水印".</summary>
    public string? Summary { get; }

    /// <summary>How many discrete items the feature acted on (watermarks removed, pages recognised…).</summary>
    public int ItemCount { get; }

    public string? Error { get; }

    public bool IsSuccess => Status == FeatureStatus.Succeeded;

    public static FeatureResult Success(string inputPath, string outputPath, string summary, int itemCount) =>
        new(FeatureStatus.Succeeded, inputPath, outputPath, summary, itemCount, null);

    public static FeatureResult NoChange(string inputPath, string summary) =>
        new(FeatureStatus.NoChange, inputPath, null, summary, 0, null);

    public static FeatureResult Fail(string inputPath, string error) =>
        new(FeatureStatus.Failed, inputPath, null, null, 0, error);
}
