using WatermarkRemover.Core.Watermarks;

namespace WatermarkRemover.Core.Processing;

public enum ProcessingStatus
{
    Succeeded,
    NoWatermarkFound,
    Failed,
}

/// <summary>Outcome of processing one input file.</summary>
public sealed class ProcessingResult
{
    private ProcessingResult(
        ProcessingStatus status,
        string inputPath,
        string? outputPath,
        IReadOnlyList<WatermarkMark> marks,
        string? error = null,
        string? errorDetails = null)
    {
        Status = status;
        InputPath = inputPath;
        OutputPath = outputPath;
        Marks = marks;
        Error = error;
        ErrorDetails = errorDetails;
    }

    public ProcessingStatus Status { get; }

    public string InputPath { get; }

    public string? OutputPath { get; }

    public IReadOnlyList<WatermarkMark> Marks { get; }

    public string? Error { get; }

    /// <summary>Full diagnostic text (exception dump) for a failure; null when not exception-driven.</summary>
    public string? ErrorDetails { get; }

    public bool IsSuccess => Status == ProcessingStatus.Succeeded;

    public static ProcessingResult Succeeded(string inputPath, string outputPath, IReadOnlyList<WatermarkMark> marks) =>
        new(ProcessingStatus.Succeeded, inputPath, outputPath, marks);

    /// <param name="outputPath">When the zero-mark run still produced a named pass-through copy,
    /// this carries it so the row keeps preview / rename / delete instead of dead-ending.</param>
    public static ProcessingResult NoWatermarkFound(string inputPath, string? outputPath = null) =>
        new(ProcessingStatus.NoWatermarkFound, inputPath, outputPath, Array.Empty<WatermarkMark>());

    public static ProcessingResult Failed(string inputPath, string error, string? errorDetails = null) =>
        new(ProcessingStatus.Failed, inputPath, null, Array.Empty<WatermarkMark>(), error, errorDetails);
}
