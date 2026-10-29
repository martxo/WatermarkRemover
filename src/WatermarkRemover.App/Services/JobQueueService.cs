using System.Diagnostics;
using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Presentation;
using WatermarkRemover.App.Recognition;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Recognition;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Runs a batch of jobs through one feature with bounded concurrency and fault isolation: one
/// bad file can never stall the rest of the batch. This is the shared execution engine behind
/// every batch operation (drag-and-drop, file picker, future batch export) — the caller owns the
/// observable collection, counters and UI marshaling.
/// Each job opens a <see cref="RecognitionRunScope"/> around its feature run so the shared
/// pipeline can attribute recognised pages to this job; afterwards the scope feeds the
/// recognition report (store + one log summary line). Report handling is fully fenced off —
/// it can never change a job's outcome.
/// </summary>
public sealed class JobQueueService
{
    private readonly IRecognitionReportStore _store;

    public JobQueueService(IRecognitionReportStore? store = null)
    {
        _store = store ?? new RecognitionReportStore();
    }

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
            long waitStart = Stopwatch.GetTimestamp();
            await throttle.WaitAsync().ConfigureAwait(false);
            long waitMs = (long)Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            AppLog.Debug($"任务排队：{Path.GetFileName(job.InputPath)} 等待 {waitMs} ms");

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
    private Task ExecuteAsync(FileJobViewModel job, IFeature feature, FeatureContext context) =>
        Task.Run(() =>
        {
            // Per job on purpose: concurrent jobs must never share one operation id.
            using var op = AppLog.BeginOperation(
                LogCategory.Job, "任务处理", Path.GetFileName(job.InputPath));

            job.MarkProcessing(FeaturePresentationCatalog.ProcessingText(feature));
            // Inside the task body on purpose: sibling jobs must not see each other's scope.
            using var scope = RecognitionRunScope.Begin(job.InputPath, feature.DisplayName);
            try
            {
                bool canHandle = feature.CanHandle(job.InputPath);
                op.Step("CanHandle", canHandle ? "可处理" : "不支持");
                if (!canHandle)
                {
                    job.Apply(FeatureResult.Fail(job.InputPath, $"{feature.DisplayName}只支持 PDF 文件"));
                    op.Complete("状态 失败，输出 -");
                    return;
                }

                long runStart = Stopwatch.GetTimestamp();
                FeatureResult result = feature.Run(job.InputPath, context);
                long runMs = (long)Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
                op.Step("执行业务", $"{feature.Id}，{runMs} ms");

                job.Apply(result);
                LogResult(result, feature);
                op.Complete($"状态 {result.Status}，输出 {result.OutputPath ?? "-"}");
            }
            catch (Exception ex)
            {
                // Features never throw for expected failures; this guards the queue against
                // unexpected defects so one bad file cannot stall the whole batch.
                AppLog.Error($"处理意外异常：{job.InputPath}", ex);
                job.Apply(FeatureResult.Fail(job.InputPath, $"意外错误：{ex.Message}", ex, feature.DisplayName));
                op.Fail($"意外异常：{ex.Message}", ex);
            }
            finally
            {
                WriteRecognitionReport(scope);
            }
        });

    /// <summary>
    /// Builds and stores the report for one run, plus one summary line in the log. Runs in the
    /// job's finally block so failed runs still report whatever pages were recognised — and is
    /// fenced so report problems can never fail the job itself.
    /// </summary>
    private void WriteRecognitionReport(RecognitionRunScope scope)
    {
        try
        {
            if (scope.Pages.Count == 0) return;

            RecognitionReport report = RecognitionReportBuilder.Build(scope);
            string path = _store.Append(report);

            string classification = report.Classification is { } c
                ? $"{c.TypeShortName}（{c.Score}）"
                : "未分类";
            AppLog.Info(
                $"识别报告：{report.InputPath} → {classification}" +
                $" · 姓名={report.PersonName ?? "-"}" +
                $" · 字段{report.Fields.Count} · 问题{report.Issues.Count} · {path}");
        }
        catch (Exception ex)
        {
            AppLog.Error("识别报告生成失败（不影响任务结果）", ex);
        }
    }

    private static void LogResult(FeatureResult result, IFeature feature)
    {
        string message = result.Status switch
        {
            FeatureStatus.Succeeded =>
                $"{feature.DisplayName}完成：{result.InputPath} → {result.OutputPath}（{result.Summary}）",
            FeatureStatus.NoChange =>
                $"{feature.DisplayName}无输出：{result.InputPath}（{result.Summary}）",
            _ => $"{feature.DisplayName}失败：{result.InputPath}（{result.Error}）",
        };
        AppLog.Info(message);
    }
}
