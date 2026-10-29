using WatermarkRemover.App.Presentation;
using WatermarkRemover.App.Recognition;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

public class JobQueueServiceTests
{
    private sealed class StubFeature : IFeature
    {
        private readonly Func<string, FeatureResult> _run;

        public StubFeature(Func<string, FeatureResult> run, string id = "stub", string name = "测试功能")
        {
            _run = run;
            Id = id;
            DisplayName = name;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public bool CanHandle(string filePath) => !filePath.EndsWith(".skip", StringComparison.OrdinalIgnoreCase);

        public FeatureResult Run(string inputPath, FeatureContext context) => _run(inputPath);
    }

    private static FileJobViewModel NewJob(string path) => new(path, Array.Empty<string>());

    [Fact]
    public async Task AppliesResultsToEveryJobAndReportsEachOne()
    {
        var feature = new StubFeature(path => FeatureResult.Success(path, path + ".out", "ok", 1));
        var queue = new JobQueueService();
        var jobs = Enumerable.Range(0, 5).Select(i => NewJob($"file{i}.pdf")).ToList();

        var finished = new List<FileJobViewModel>();
        await queue.RunAsync(jobs, feature, FeatureContext.Capture(new AppSettings()),
            maxConcurrency: 2, onJobFinished: finished.Add);

        Assert.Equal(jobs.Count, finished.Count);
        Assert.All(jobs, job => Assert.Equal(JobState.Succeeded, job.State));
    }

    [Fact]
    public async Task UnhandledPathIsFailedWithFeatureName()
    {
        var feature = new StubFeature(path => FeatureResult.Success(path, path + ".out", "ok", 1));
        var queue = new JobQueueService();
        var job = NewJob("doc.skip");

        await queue.RunAsync(new[] { job }, feature, FeatureContext.Capture(new AppSettings()),
            maxConcurrency: 1, onJobFinished: _ => { });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("只支持", job.Detail);
    }

    [Fact]
    public async Task ThrowingFeatureYieldsFailedJobAndStillReportsCompletion()
    {
        var feature = new StubFeature(path => throw new InvalidOperationException("boom"));
        var queue = new JobQueueService();
        var job = NewJob("doc.pdf");
        var finished = new List<FileJobViewModel>();

        await queue.RunAsync(new[] { job }, feature, FeatureContext.Capture(new AppSettings()),
            maxConcurrency: 1, onJobFinished: finished.Add);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("意外错误", job.Detail);
        Assert.Equal(job, Assert.Single(finished));
    }

    [Fact]
    public async Task CardDetailShowsFeatureProcessingTextWhileRunning()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var feature = new StubFeature(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return FeatureResult.Success(_, _ + ".out", "ok", 1);
        });
        var queue = new JobQueueService();
        var job = NewJob("doc.pdf");

        Task run = queue.RunAsync(new[] { job }, feature, FeatureContext.Capture(new AppSettings()),
            maxConcurrency: 1, onJobFinished: _ => { });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(JobState.Processing, job.State);
        // StubFeature has no presentation entry: the catalog's generic text must surface, and the
        // watermark wording must never appear for another feature.
        IFeature asInterface = feature;
        Assert.Equal(FeaturePresentationCatalog.ProcessingText(asInterface), job.Detail);
        Assert.DoesNotContain("水印", job.Detail);

        release.Set();
        await run;
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task ConcurrencyNeverExceedsLimit()
    {
        int running = 0;
        int peak = 0;
        object sync = new();

        var feature = new StubFeature(path =>
        {
            lock (sync)
            {
                running++;
                peak = Math.Max(peak, running);
            }
            Thread.Sleep(20);
            lock (sync) running--;
            return FeatureResult.Success(path, path + ".out", "ok", 1);
        });

        var queue = new JobQueueService();
        var jobs = Enumerable.Range(0, 8).Select(i => NewJob($"file{i}.pdf")).ToList();

        await queue.RunAsync(jobs, feature, FeatureContext.Capture(new AppSettings()),
            maxConcurrency: 3, onJobFinished: _ => { });

        Assert.True(peak <= 3, $"peak concurrency was {peak}");
    }

    /// <summary>Feature that feeds one recorded licence page into the ambient report scope.</summary>
    private sealed class ScopeRecordingFeature : IFeature
    {
        public string Id => "scope-rec";

        public string DisplayName => "识别记录测试";

        public bool CanHandle(string filePath) => true;

        public FeatureResult Run(string inputPath, FeatureContext context)
        {
            RecognitionRunScope.Current?.RecordPage(600, 800, "竖版", "整图直通", "微信OCR",
                Array.Empty<LayoutRegion>(),
                new[]
                {
                    new RecognizedLine("营业执照", new RectD(0.1, 0.05, 0.3, 0.04)),
                    new RecognizedLine("统一社会信用代码 91350200MA1234567X",
                        new RectD(0.1, 0.1, 0.4, 0.04)),
                });
            return FeatureResult.Success(inputPath, inputPath + ".out", "ok", 1);
        }
    }

    /// <summary>Store that fails on every call — the job must not care.</summary>
    private sealed class ThrowingStore : IRecognitionReportStore
    {
        public string Append(RecognitionReport report) => throw new InvalidOperationException("disk full");

        public IReadOnlyList<RecognitionReport> ReadRecent(int maxLines) =>
            throw new InvalidOperationException("disk full");
    }

    [Fact]
    public async Task SuccessfulJobAppendsOneRecognitionReport()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmr-jobqueue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecognitionReportStore(dir);
            var queue = new JobQueueService(store);
            var job = NewJob("scan.pdf");

            await queue.RunAsync(new[] { job }, new ScopeRecordingFeature(),
                FeatureContext.Capture(new AppSettings()), maxConcurrency: 1, onJobFinished: _ => { });

            Assert.Equal(JobState.Succeeded, job.State);

            // The whole loop: job ran → scope collected → report built → persisted as JSONL.
            RecognitionReport report = Assert.Single(store.ReadRecent(10));
            Assert.Equal("scan.pdf", report.InputPath);
            Assert.Equal(1, report.PageCount);
            Assert.Equal("business-license", report.Classification!.TypeId);
            Assert.Equal(600, report.Pages[0].WidthPx);
            Assert.Equal("竖版", report.Pages[0].Orientation);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }

    [Fact]
    public async Task ReportStoreFailureNeverFailsTheJob()
    {
        var queue = new JobQueueService(new ThrowingStore());
        var job = NewJob("scan.pdf");

        await queue.RunAsync(new[] { job }, new ScopeRecordingFeature(),
            FeatureContext.Capture(new AppSettings()), maxConcurrency: 1, onJobFinished: _ => { });

        Assert.Equal(JobState.Succeeded, job.State);
    }
}
