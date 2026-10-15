using System.IO;
using WatermarkRemover.Core.Processing;

namespace WatermarkRemover.App.ViewModels;

public enum JobState
{
    Queued,
    Processing,
    Succeeded,
    Skipped,
    Failed,
}

/// <summary>One input file in the processing queue.</summary>
public sealed class FileJobViewModel : ObservableObject
{
    private JobState _state = JobState.Queued;
    private string _detail = "等待处理";
    private string? _outputPath;
    private int _watermarkCount;
    private DateTime? _processedAt;

    public FileJobViewModel(string inputPath)
    {
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
    }

    public string InputPath { get; }

    public string FileName { get; }

    public JobState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => State switch
    {
        JobState.Queued => "待处理",
        JobState.Processing => "处理中",
        JobState.Succeeded => "完成",
        JobState.Skipped => "无水印",
        JobState.Failed => "失败",
        _ => State.ToString(),
    };

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        private set => SetProperty(ref _outputPath, value);
    }

    public int WatermarkCount
    {
        get => _watermarkCount;
        private set => SetProperty(ref _watermarkCount, value);
    }

    /// <summary>Time the job reached a terminal state (done, skipped, or failed).</summary>
    public DateTime? ProcessedAt
    {
        get => _processedAt;
        private set
        {
            if (SetProperty(ref _processedAt, value))
                OnPropertyChanged(nameof(ProcessedAtText));
        }
    }

    public string ProcessedAtText => ProcessedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;

    public void MarkProcessing()
    {
        State = JobState.Processing;
        Detail = "正在分析并去除水印…";
    }

    public void Apply(ProcessingResult result)
    {
        ProcessedAt = DateTime.Now;
        switch (result.Status)
        {
            case ProcessingStatus.Succeeded:
                State = JobState.Succeeded;
                OutputPath = result.OutputPath;
                WatermarkCount = result.Marks.Count;
                Detail = $"已去除 {result.Marks.Count} 处水印 → {result.OutputPath}";
                break;

            case ProcessingStatus.NoWatermarkFound:
                State = JobState.Skipped;
                Detail = "未检测到水印，已跳过";
                break;

            case ProcessingStatus.Failed:
                State = JobState.Failed;
                Detail = result.Error ?? "处理失败";
                break;
        }
    }
}
