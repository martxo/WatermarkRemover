using WatermarkRemover.App.Logging;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Runs a batch of jobs through one feature with bounded concurrency and fault isolation: one
/// bad file can never stall the rest of the batch. This is the shared execution engine behind
/// every batch operation (drag-and-drop, file picker, future batch export) — the caller owns the
/// observable collection, counters and UI marshaling.
/// </summary>
public sealed class JobQueueService
{
    /// <summary>
    /// Processes <paramref name="jobs"/> concurrently (at most <paramref name="maxConcurrency"/>
    /// at a time), invoking <paramref name="onJobFinished"/> exactly once per job after its
    /// result has been applied — including jobs that failed unexpectedly.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<FileJobViewModel> jobs,
        IFeature feature,
        FeatureContext context,
        int maxConcurrency,
        Action<FileJobViewModel> onJobFinished)
    {
        using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        await Task.WhenAll(jobs.Select(async job =>
        {
            await throttle.WaitAsync().ConfigureAwait(false);
            try
            {
                await ExecuteAsync(job, feature, context).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
                onJobFinished(job);
            }
        })).ConfigureAwait(false);
    }

    /// <summary>One job on a worker thread: mark, run the feature, apply the result.</summary>
    private static Task ExecuteAsync(FileJobViewModel job, IFeature feature, FeatureContext context) =>
        Task.Run(() =>
        {
            job.MarkProcessing();
            try
            {
                if (!feature.CanHandle(job.InputPath))
                {
                    job.Apply(FeatureResult.Fail(job.InputPath, $"{feature.DisplayName}只支持 PDF 文件"));
                    return;
                }

                FeatureResult result = feature.Run(job.InputPath, context);
                job.Apply(result);
                LogResult(result, feature);
            }
            catch (Exception ex)
            {
                // Features never throw for expected failures; this guards the queue against
                // unexpected defects so one bad file cannot stall the whole batch.
                AppLog.Error($"处理意外异常：{job.InputPath}", ex);
                job.Apply(FeatureResult.Fail(job.InputPath, $"意外错误：{ex.Message}", ex, feature.DisplayName));
            }
        });

    private static void LogResult(FeatureResult result, IFeature feature)
    {
        string message = result.Status switch
        {
            FeatureStatus.Succeeded =>
                $"{feature.DisplayName}完成：{result.InputPath} → {result.OutputPath}（{result.Summary}）",
            FeatureStatus.NoChange => $"{feature.DisplayName}无输出：{result.InputPath}（{result.Summary}）",
            _ => $"{feature.DisplayName}失败：{result.InputPath}（{result.Error}）",
        };
        AppLog.Info(message);
    }
}
