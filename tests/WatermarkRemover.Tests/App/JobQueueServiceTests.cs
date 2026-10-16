using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;
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
}
