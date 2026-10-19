using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Recognition;

namespace WatermarkRemover.Tests.Core.Recognition;

public class RecognitionRunScopeTests
{
    [Fact]
    public void BeginExposesScopeAndDisposeClearsAmbientValue()
    {
        Assert.Null(RecognitionRunScope.Current);

        RecognitionRunScope scope = RecognitionRunScope.Begin("a.pdf", "OCR 识别");
        try
        {
            Assert.Same(scope, RecognitionRunScope.Current);
            Assert.Equal("a.pdf", scope.InputPath);
            Assert.Equal("OCR 识别", scope.FeatureName);
            Assert.Empty(scope.Pages);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.Null(RecognitionRunScope.Current);
    }

    [Fact]
    public void NestedScopeDisposeRestoresTheOuterScope()
    {
        using var outer = RecognitionRunScope.Begin("outer.pdf", "外层");

        RecognitionRunScope inner = RecognitionRunScope.Begin("inner.pdf", "内层");
        Assert.Same(inner, RecognitionRunScope.Current);

        inner.Dispose();
        Assert.Same(outer, RecognitionRunScope.Current);
    }

    [Fact]
    public async Task SiblingTasksNeverSeeEachOthersScope()
    {
        // The job queue opens one scope per task inside Task.Run: a sibling's pages must not
        // leak into this flow, and the outer ambient value must survive the child task.
        using var outer = RecognitionRunScope.Begin("outer.pdf", "外层");

        RecognitionRunScope? inside = null;
        await Task.Run(() =>
        {
            using var scope = RecognitionRunScope.Begin("sibling.pdf", "兄弟");
            scope.RecordPage(10, 20, "竖版", "整图直通", "引擎",
                Array.Empty<LayoutRegion>(), Array.Empty<RecognizedLine>());
            inside = RecognitionRunScope.Current;
        });

        Assert.NotSame(outer, inside);
        Assert.Equal("sibling.pdf", inside!.InputPath);
        Assert.Single(inside.Pages);
        Assert.Empty(outer.Pages);
        Assert.Same(outer, RecognitionRunScope.Current);
    }

    [Fact]
    public void RecordPageClipsLineCountAndLineLength()
    {
        // JSONL size guards: at most 500 lines per page, 200 characters per line.
        RecognitionRunScope scope = RecognitionRunScope.Begin("big.pdf", "OCR 识别");
        try
        {
            List<RecognizedLine> lines = Enumerable.Range(0, 600)
                .Select(i => new RecognizedLine(new string('字', i == 0 ? 300 : 5), null))
                .ToList();

            scope.RecordPage(null, null, null, "整图直通", "引擎",
                Array.Empty<LayoutRegion>(), lines);

            RecordedPage page = Assert.Single(scope.Pages);
            Assert.Equal(500, page.Lines.Count);
            Assert.Equal(200, page.Lines[0].Text.Length);
            Assert.Equal(5, page.Lines[1].Text.Length);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void PagesAreNumberedInRecognitionOrder()
    {
        RecognitionRunScope scope = RecognitionRunScope.Begin("multi.pdf", "OCR 识别");
        try
        {
            for (int i = 0; i < 3; i++)
                scope.RecordPage(null, null, null, "整图直通", "引擎",
                    Array.Empty<LayoutRegion>(), Array.Empty<RecognizedLine>());

            Assert.Equal(new[] { 1, 2, 3 }, scope.Pages.Select(p => p.Index));
        }
        finally
        {
            scope.Dispose();
        }
    }
}
