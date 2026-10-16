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
    private FeatureResult(
        FeatureStatus status,
        string inputPath,
        string? outputPath,
        string? summary,
        int itemCount,
        string? error,
        string? errorDetails,
        string? featureName)
    {
        Status = status;
        InputPath = inputPath;
        OutputPath = outputPath;
        Summary = summary;
        ItemCount = itemCount;
        Error = error;
        ErrorDetails = errorDetails;
        FeatureName = featureName;
    }

    public FeatureStatus Status { get; }

    public string InputPath { get; }

    /// <summary>The generated file, when <see cref="FeatureStatus.Succeeded"/>.</summary>
    public string? OutputPath { get; }

    /// <summary>Short human description of what happened, e.g. "已去除 3 处水印".</summary>
    public string? Summary { get; }

    /// <summary>How many discrete items the feature acted on (watermarks removed, pages recognised…).</summary>
    public int ItemCount { get; }

    /// <summary>Short human error shown inline on the job row.</summary>
    public string? Error { get; }

    /// <summary>
    /// Full diagnostic text for a failure — exception type, HRESULT, stack trace and inner
    /// exceptions. Carried verbatim into the "copy error report" the UI offers so a user can send
    /// an actionable report. Null when the failure was not caused by an exception.
    /// </summary>
    public string? ErrorDetails { get; }

    /// <summary>Display name of the feature that produced this result; used in error reports.</summary>
    public string? FeatureName { get; }

    public bool IsSuccess => Status == FeatureStatus.Succeeded;

    public static FeatureResult Success(string inputPath, string outputPath, string summary, int itemCount) =>
        new(FeatureStatus.Succeeded, inputPath, outputPath, summary, itemCount, null, null, null);

    public static FeatureResult NoChange(string inputPath, string summary) =>
        new(FeatureStatus.NoChange, inputPath, null, summary, 0, null, null, null);

    /// <summary>Failure with only a human message — nothing to attach for diagnostics.</summary>
    public static FeatureResult Fail(string inputPath, string error) =>
        new(FeatureStatus.Failed, inputPath, null, null, 0, error, null, null);

    /// <summary>Failure caused by an exception; the full exception becomes the diagnostic details.</summary>
    public static FeatureResult Fail(string inputPath, string error, Exception ex, string? featureName = null) =>
        new(FeatureStatus.Failed, inputPath, null, null, 0, error, ex.ToString(), featureName);

    /// <summary>Failure with pre-formatted diagnostic details (e.g. from a processing pipeline).</summary>
    public static FeatureResult FailWithDetails(
        string inputPath, string error, string? details, string? featureName = null) =>
        new(FeatureStatus.Failed, inputPath, null, null, 0, error, details, featureName);
}
