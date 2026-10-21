using System.Text;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Processing;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Pdf;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.Integration;

/// <summary>
/// End-to-end tests through the real PdfSharpCore adapter. Fixtures are byte-exact PDFs built by
/// hand, so success here proves the pipeline works on genuine PDF structure, not on mocks.
/// </summary>
public class PdfPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-pipeline-" + Guid.NewGuid().ToString("N"));

    public PdfPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string WriteInput(byte[] bytes, string name = "input.pdf")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static WatermarkProcessor CreateProcessor(
        AppSettings? settings = null,
        Func<string, AutoNameResult?>? autoNamer = null) =>
        new(new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            settings ?? new AppSettings(), autoNamer);

    [Fact]
    public void Extract_reads_geometry_resources_and_annotations()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var raw = new PdfDocumentSource().Extract(input);

        var page = Assert.Single(raw.Pages);
        Assert.Equal(842, page.MediaBox.Width);
        Assert.Equal(595, page.MediaBox.Height);
        Assert.Contains("Im1", page.XObjectNames);
        Assert.Contains("Im2", page.XObjectNames);

        var annotation = Assert.Single(page.Annotations);
        Assert.Equal(723, annotation.Bounds.X);
        Assert.Equal("https://v3.camscanner.com/user/download", annotation.Uri);

        string content = ContentRewriterToText(page.ContentStream);
        Assert.Contains("/Im2 Do", content);
    }

    [Fact]
    public void Process_removes_the_badge_and_keeps_the_body()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var result = CreateProcessor().Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        var output = new PdfDocumentSource().Extract(result.OutputPath!);
        var page = Assert.Single(output.Pages);

        // Body image is untouched, watermark resource and its annotation are gone.
        Assert.Contains("Im1", page.XObjectNames);
        Assert.DoesNotContain("Im2", page.XObjectNames);
        Assert.Empty(page.Annotations);

        string content = ContentRewriterToText(page.ContentStream);
        Assert.Contains("/Im1 Do", content);
        Assert.DoesNotContain("/Im2 Do", content);
    }

    [Fact]
    public void Process_is_idempotent_when_run_twice()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var first = CreateProcessor().Process(input);
        Assert.Equal(ProcessingStatus.Succeeded, first.Status);

        var second = CreateProcessor().Process(first.OutputPath!);

        // A file that is already clean reports "no watermark" and writes nothing.
        Assert.Equal(ProcessingStatus.NoWatermarkFound, second.Status);
        Assert.Null(second.OutputPath);
    }

    [Fact]
    public void Process_names_and_copies_a_clean_document_when_the_namer_recognises_it()
    {
        byte[] bytes = PdfFixture.CamScannerLike().WithContent("q Q\n").Build();
        string input = WriteInput(bytes, "clean.pdf");

        var result = CreateProcessor(autoNamer: _ => AutoNameResult.Create("林孝明", "执照")).Process(input);

        // A clean document still lands as a content-named copy the row can preview and rename.
        Assert.Equal(ProcessingStatus.NoWatermarkFound, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath!), "the named copy must exist on disk");
        Assert.Equal("林孝明执照.pdf", Path.GetFileName(result.OutputPath));
        Assert.Equal(bytes, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void Process_skips_the_copy_when_a_clean_document_is_already_named_ideally()
    {
        byte[] bytes = PdfFixture.CamScannerLike().WithContent("q Q\n").Build();
        string input = WriteInput(bytes, "林孝明执照.pdf");

        var result = CreateProcessor(autoNamer: _ => AutoNameResult.Create("林孝明", "执照")).Process(input);

        // The file already carries its ideal name: a "(2)" duplicate serves nobody.
        Assert.Equal(ProcessingStatus.NoWatermarkFound, result.Status);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public void Process_still_skips_a_clean_document_when_naming_is_unavailable()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().WithContent("q Q\n").Build(), "clean.pdf");

        var result = CreateProcessor().Process(input);

        // No namer → nothing to name → the old skip stands (no suffix-chain copies).
        Assert.Equal(ProcessingStatus.NoWatermarkFound, result.Status);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public void Process_writes_into_the_configured_directory()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());
        string target = Path.Combine(_dir, "out");
        Directory.CreateDirectory(target);

        var result = CreateProcessor(new AppSettings
        {
            OutputMode = OutputMode.CustomDirectory,
            OutputDirectory = target,
            OutputSuffix = "_clean",
        }).Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.Equal(Path.Combine(target, "input_clean.pdf"), result.OutputPath);
    }

    [Fact]
    public void Process_reports_failure_for_a_missing_file()
    {
        var result = CreateProcessor().Process(Path.Combine(_dir, "nope.pdf"));

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Process_reports_failure_for_a_corrupt_file()
    {
        string input = WriteInput(Encoding.Latin1.GetBytes("this is not a pdf at all"));

        var result = CreateProcessor().Process(input);

        Assert.Equal(ProcessingStatus.Failed, result.Status);
    }

    [Fact]
    public void Process_keeps_an_unrelated_link_annotation()
    {
        var fixture = PdfFixture.CamScannerLike()
            .WithLinkAnnotation(50, 500, 200, 540, "https://example.com/keep-me");
        string input = WriteInput(fixture.Build());

        var result = CreateProcessor().Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        var output = new PdfDocumentSource().Extract(result.OutputPath!);
        var annotation = Assert.Single(Assert.Single(output.Pages).Annotations);
        Assert.Equal("https://example.com/keep-me", annotation.Uri);
    }

    [Fact]
    public void Process_keeps_the_watermark_annotation_when_the_option_is_off()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var result = CreateProcessor(new AppSettings { RemoveLinkedAnnotations = false }).Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        var output = new PdfDocumentSource().Extract(result.OutputPath!);
        var page = Assert.Single(output.Pages);

        // The badge drawing is gone, but the hyperlink stays.
        Assert.DoesNotContain("Im2", page.XObjectNames);
        Assert.Single(page.Annotations);
    }

    [Fact]
    public void Multi_page_documents_are_processed_page_by_page()
    {
        // One clean page and one watermarked page in the same file.
        var fixture = PdfFixture.CamScannerLike();
        fixture.Build(); // sanity: fixture shape holds

        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var result = CreateProcessor().Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
    }

    [Fact]
    public void Process_does_not_overwrite_an_existing_output()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());
        File.WriteAllBytes(Path.Combine(_dir, "input_no-watermark.pdf"), Encoding.Latin1.GetBytes("occupied"));

        var result = CreateProcessor().Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.Equal(Path.Combine(_dir, "input_no-watermark (2).pdf"), result.OutputPath);
        Assert.Equal("occupied", Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(_dir, "input_no-watermark.pdf"))));
    }

    [Fact]
    public void Process_names_the_output_after_the_auto_namer_result()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());
        var processor = new WatermarkProcessor(
            new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            new AppSettings(), _ => AutoNameResult.Create("林孝明", "执照"));

        var result = processor.Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.Equal(Path.Combine(_dir, "林孝明执照.pdf"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public void Process_falls_back_to_default_naming_when_the_auto_namer_returns_null()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());
        var processor = new WatermarkProcessor(
            new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            new AppSettings(), _ => null);

        var result = processor.Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.EndsWith("_no-watermark.pdf", Path.GetFileName(result.OutputPath));
    }

    [Fact]
    public void Process_falls_back_to_default_naming_when_the_auto_namer_throws()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());
        var processor = new WatermarkProcessor(
            new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            new AppSettings(), _ => throw new InvalidOperationException("OCR 失败"));

        var result = processor.Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.EndsWith("_no-watermark.pdf", Path.GetFileName(result.OutputPath));
    }

    [Fact]
    public void Auto_named_output_does_not_overwrite_an_existing_file()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());
        File.WriteAllBytes(Path.Combine(_dir, "林孝明执照.pdf"), Encoding.Latin1.GetBytes("occupied"));
        var processor = new WatermarkProcessor(
            new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            new AppSettings(), _ => AutoNameResult.Create("林孝明", "执照"));

        var result = processor.Process(input);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.Equal(Path.Combine(_dir, "林孝明执照 (2).pdf"), result.OutputPath);
        Assert.Equal("occupied", Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(_dir, "林孝明执照.pdf"))));
    }

    private static string ContentRewriterToText(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
