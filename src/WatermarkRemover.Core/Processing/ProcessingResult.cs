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
        string? error = null)
    {
        Status = status;
        InputPath = inputPath;
        OutputPath = outputPath;
        Marks = marks;
        Error = error;
    }

    public ProcessingStatus Status { get; }

    public string InputPath { get; }

    public string? OutputPath { get; }

    public IReadOnlyList<WatermarkMark> Marks { get; }

    public string? Error { get; }

    public bool IsSuccess => Status == ProcessingStatus.Succeeded;

    public static ProcessingResult Succeeded(string inputPath, string outputPath, IReadOnlyList<WatermarkMark> marks) =>
        new(ProcessingStatus.Succeeded, inputPath, outputPath, marks);

    public static ProcessingResult NoWatermarkFound(string inputPath) =>
        new(ProcessingStatus.NoWatermarkFound, inputPath, null, Array.Empty<WatermarkMark>());

    public static ProcessingResult Failed(string inputPath, string error) =>
        new(ProcessingStatus.Failed, inputPath, null, Array.Empty<WatermarkMark>(), error);
}
