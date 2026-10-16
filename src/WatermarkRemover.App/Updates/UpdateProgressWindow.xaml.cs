using System.Threading;
using System.Windows;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
        Cts = new CancellationTokenSource();
        Closed += (_, _) => Cts.Cancel(); // closing the window (X button) cancels the download
    }

    public CancellationTokenSource Cts { get; }

    /// <summary>Shows the stage a pre-download flow is in (e.g. "正在检查更新…").</summary>
    public void ShowStage(UpdateStage stage, string? titleOverride = null) =>
        Dispatcher.Invoke(() => ApplyStage(titleOverride ?? TitleFor(stage), stage, null));

    /// <summary>Thread-safe progress update; download runs on a worker context.</summary>
    public void Report(UpdateProgress progress) => Dispatcher.Invoke(() =>
    {
        string title = TitleFor(progress.Stage);
        ApplyStage(title, progress.Stage, progress);
    });

    private void ApplyStage(string title, UpdateStage stage, UpdateProgress? progress)
    {
        TitleText.Text = title;

        // Checking / Verifying are indeterminate: no honest percentage exists there.
        bool determinate = stage == UpdateStage.Downloading && progress?.Percent is not null;
        Bar.IsIndeterminate = !determinate;
        if (determinate)
            Bar.Value = progress!.Percent!.Value;

        DetailText.Text = progress is null ? string.Empty : DetailFor(progress);
        SourceText.Text = string.IsNullOrEmpty(progress?.SourceDescription)
            ? string.Empty
            : $"来源：{progress!.SourceDescription}";

        PercentText.Text = stage switch
        {
            UpdateStage.Downloading when progress?.Percent is double p => $"{p:F0}%",
            _ => string.Empty,
        };

        // Verifying finishes in about a second; a cancel click there cannot be honoured in
        // time and would only look broken. Restarting is also a one-way step.
        CancelButton.IsEnabled = stage is UpdateStage.Checking or UpdateStage.Downloading;
        CancelButton.Opacity = CancelButton.IsEnabled ? 1.0 : 0.5;
    }

    private string TitleFor(UpdateStage stage) => stage switch
    {
        UpdateStage.Checking => "正在检查更新…",
        UpdateStage.Downloading => "正在下载更新…",
        UpdateStage.Verifying => "正在校验更新包…",
        UpdateStage.ReadyToRestart => "更新完成，即将重启",
        _ => "正在准备更新…",
    };

    private static string DetailFor(UpdateProgress progress) => progress.Stage switch
    {
        UpdateStage.Downloading => ProgressTextFormatter.FormatDownloadDetail(
            progress.BytesReceived, progress.TotalBytes, progress.SpeedBytesPerSecond),
        _ => string.Empty,
    };

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
