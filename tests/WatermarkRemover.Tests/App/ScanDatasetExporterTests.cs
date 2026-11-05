using System.IO;
using System.Linq;
using System.Text;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Pdf;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Training-dataset export contract: off means no files, on means every page carries the probe,
/// the raw grayscale frame and its vote record, and truth is what the written PDF actually says.
/// </summary>
public sealed class ScanDatasetExporterTests
{
    [Fact]
    public void Switch_off_writes_nothing()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = false };
            ScanDatasetExporter? exporter =
                ScanDatasetExporter.Create(settings, rootOverride: root, envOverride: "0");
            Assert.Null(exporter);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Env_value_one_alone_enables_the_export()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = false };
            ScanDatasetExporter? exporter =
                ScanDatasetExporter.Create(settings, rootOverride: root, envOverride: "1");
            Assert.NotNull(exporter);
            Assert.True(Directory.Exists(exporter.SessionDirectory));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Switch_on_writes_every_required_file_per_page()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = true };
            ScanDatasetExporter exporter = ScanDatasetExporter.Create(
                settings, rootOverride: root, envOverride: "0")!;

            // Capture-order frames first (duplex interleave happens later in production).
            var frames = Enumerable.Range(1, 3).Select(index => Frame(index)).ToList();
            foreach (var (frame, index) in frames.Select((frame, index) => (frame, index)))
                exporter.SaveCaptureFrame(index + 1, frame);

            string pdf = BuildPdf(exporter.SessionDirectory);
            exporter.Complete(Records(frames), Session(), pdf);

            for (var page = 1; page <= 3; page++)
            {
                Assert.True(File.Exists(Path.Combine(exporter.SessionDirectory, $"p{page}-probe.png")));
                Assert.True(File.Exists(Path.Combine(exporter.SessionDirectory, $"p{page}-frame.png")));
                Assert.True(File.Exists(Path.Combine(exporter.SessionDirectory, $"p{page}.txt")));
            }
            Assert.True(File.Exists(Path.Combine(exporter.SessionDirectory, "session.txt")));
            Assert.True(File.Exists(Path.Combine(exporter.SessionDirectory, "truth.txt")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Probe_bytes_are_the_production_bytes()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = true };
            ScanDatasetExporter exporter = ScanDatasetExporter.Create(
                settings, rootOverride: root, envOverride: "0")!;

            var frames = Enumerable.Range(1, 3).Select(index => Frame(index)).ToList();
            foreach (var (frame, index) in frames.Select((frame, index) => (frame, index)))
                exporter.SaveCaptureFrame(index + 1, frame);

            string pdf = BuildPdf(exporter.SessionDirectory);
            exporter.Complete(Records(frames), Session(), pdf);

            for (var page = 1; page <= 3; page++)
            {
                byte[] expected = Encoding.UTF8.GetBytes($"probe-{page}");
                Assert.Equal(expected,
                    File.ReadAllBytes(Path.Combine(exporter.SessionDirectory, $"p{page}-probe.png")));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Truth_matches_written_pdf_rotate()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = true };
            ScanDatasetExporter exporter = ScanDatasetExporter.Create(
                settings, rootOverride: root, envOverride: "0")!;

            var frames = Enumerable.Range(1, 3).Select(index => Frame(index)).ToList();
            foreach (var (frame, index) in frames.Select((frame, index) => (frame, index)))
                exporter.SaveCaptureFrame(index + 1, frame);

            string pdf = BuildPdf(exporter.SessionDirectory, perPageTurns: new[] { 1, 0, 3 });
            exporter.Complete(Records(frames), Session(), pdf);

            IReadOnlyList<int> onDisk = PdfPageRotationReader.Read(pdf);
            Assert.Equal(new[] { 90, 0, 270 }, onDisk.ToArray());

            string[] truthLines = File.ReadAllLines(
                Path.Combine(exporter.SessionDirectory, "truth.txt"));
            Assert.Equal("# pageIndex,rotate", truthLines[0]);
            Assert.Equal("1,90", truthLines[1]);
            Assert.Equal("2,0", truthLines[2]);
            Assert.Equal("3,270", truthLines[3]);

            for (var page = 1; page <= 3; page++)
            {
                string[] lines = File.ReadAllLines(
                    Path.Combine(exporter.SessionDirectory, $"p{page}.txt"));
                string[] values = lines[1].Split(',');
                Assert.Equal(page.ToString(), values[0]);
                Assert.Equal(onDisk[page - 1].ToString(), values[5]);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Capture_order_is_remapped_to_final_page_order()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = true };
            ScanDatasetExporter exporter = ScanDatasetExporter.Create(
                settings, rootOverride: root, envOverride: "0")!;

            var frames = Enumerable.Range(1, 3).Select(index => Frame(index)).ToList();
            foreach (var (frame, index) in frames.Select((frame, index) => (frame, index)))
                exporter.SaveCaptureFrame(index + 1, frame);

            string pdf = BuildPdf(exporter.SessionDirectory);

            // Final reading order: page 1 came from capture 2, page 2 from capture 1.
            exporter.Complete(
                new[]
                {
                    Record(1, 2, frames[1]),
                    Record(2, 1, frames[0]),
                    Record(3, 3, frames[2]),
                },
                Session(), pdf);

            Assert.Equal(ScanDatasetExporter.EncodeGrayPng(frames[1]),
                File.ReadAllBytes(Path.Combine(exporter.SessionDirectory, "p1-frame.png")));
            Assert.Equal(ScanDatasetExporter.EncodeGrayPng(frames[0]),
                File.ReadAllBytes(Path.Combine(exporter.SessionDirectory, "p2-frame.png")));
            Assert.False(File.Exists(Path.Combine(exporter.SessionDirectory, "c1-frame.png")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Truth_refresh_after_manual_rotation_updates_truth_and_records()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = true };
            ScanDatasetExporter exporter = ScanDatasetExporter.Create(
                settings, rootOverride: root, envOverride: "0")!;

            var frames = Enumerable.Range(1, 3).Select(index => Frame(index)).ToList();
            foreach (var (frame, index) in frames.Select((frame, index) => (frame, index)))
                exporter.SaveCaptureFrame(index + 1, frame);

            string pdf = BuildPdf(exporter.SessionDirectory, perPageTurns: new[] { 0, 0, 0 });
            exporter.Complete(Records(frames), Session(), pdf);
            Assert.Equal(new[] { 0, 0, 0 }, PdfPageRotationReader.Read(pdf).ToArray());

            // Simulate the user rotating page 2 clockwise once: the file on disk changes, then
            // the preview hook records the action and refreshes truth from that file.
            pdf = BuildPdf(exporter.SessionDirectory, perPageTurns: new[] { 0, 1, 0 });
            ScanDatasetExporter.AppendManualRotation(
                exporter.SessionDirectory, 2, 90, "Manual");
            ScanDatasetExporter.RefreshTruth(
                exporter.SessionDirectory, pdf, "manual-rotate");

            string[] truth = File.ReadAllLines(
                Path.Combine(exporter.SessionDirectory, "truth.txt"));
            Assert.Equal("1,0", truth[1]);
            Assert.Equal("2,90", truth[2]);
            Assert.Equal("3,0", truth[3]);

            string[] rotations = File.ReadAllLines(
                Path.Combine(exporter.SessionDirectory, "manual-rotations.txt"));
            Assert.Equal("# time,pageIndex,degrees,source", rotations[0]);
            Assert.Contains(",2,90,Manual", rotations[1]);

            string[] refresh = File.ReadAllLines(
                Path.Combine(exporter.SessionDirectory, "truth-refresh.txt"));
            Assert.Equal("# time,reason,pageCount", refresh[0]);
            Assert.Contains("manual-rotate", refresh[1]);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void No_manual_rotation_keeps_truth_and_creates_no_side_files()
    {
        string root = NewTempRoot();
        try
        {
            var settings = new AppSettings { ScanDatasetExportEnabled = true };
            ScanDatasetExporter exporter = ScanDatasetExporter.Create(
                settings, rootOverride: root, envOverride: "0")!;

            var frames = Enumerable.Range(1, 3).Select(index => Frame(index)).ToList();
            foreach (var (frame, index) in frames.Select((frame, index) => (frame, index)))
                exporter.SaveCaptureFrame(index + 1, frame);

            string pdf = BuildPdf(exporter.SessionDirectory, perPageTurns: new[] { 1, 0, 3 });
            exporter.Complete(Records(frames), Session(), pdf);

            Assert.Equal(new[] { 90, 0, 270 },
                PdfPageRotationReader.Read(pdf).ToArray());
            Assert.False(File.Exists(
                Path.Combine(exporter.SessionDirectory, "manual-rotations.txt")));
            Assert.False(File.Exists(
                Path.Combine(exporter.SessionDirectory, "truth-refresh.txt")));
        }
        finally
        {
            Delete(root);
        }
    }

    private static ScanDatasetPage[] Records(IReadOnlyList<RasterImage> frames) =>
        frames.Select((frame, index) => Record(index + 1, index + 1, frame)).ToArray();

    private static ScanDatasetPage Record(int pageIndex, int capturePage, RasterImage frame) =>
        new(pageIndex, capturePage, Encoding.UTF8.GetBytes($"probe-{pageIndex}"),
            ProbeTurns: 0, ProbeConfidence: 0.93f, BaselineHorizontal: true,
            LandscapeGuard: false, FinalRotate: new[] { 90, 0, 270 }[pageIndex - 1],
            Source: "Classifier");

    private static ScanDatasetSession Session() =>
        new("测试扫描仪", "Dpi300", "LongEdge", "LongEdge", 3, 0, 1234);

    /// <summary>Distinct gray level per seed so remap assertions cannot pass by accident.</summary>
    private static RasterImage Frame(int seed)
    {
        const int width = 40, height = 60;
        var bgra = new byte[width * height * 4];
        byte level = (byte)(seed * 60);
        for (var i = 0; i < width * height; i++)
        {
            bgra[i * 4 + 0] = level;
            bgra[i * 4 + 1] = level;
            bgra[i * 4 + 2] = level;
            bgra[i * 4 + 3] = 255;
        }
        return new RasterImage(width, height, bgra);
    }

    private static string BuildPdf(string directory, int[]? perPageTurns = null)
    {
        var document = new ScanDocument
        {
            Pages = Enumerable.Range(0, 3).Select(index => new ScannedPage
            {
                Image = Frame(index + 1), Index = index, SheetIndex = index,
            }).ToList(),
            Settings = new ScanSettings
            {
                Resolution = ScanResolution.Dpi300,
                Color = ColorMode.BlackWhite,
            },
            BaseName = "dataset",
        };
        var options = new PdfBuildOptions { HighCompression = true };
        var encoder = new WatermarkRemover.Scan.Engine.ScanImageEncoder();
        double dpi = PdfDocumentBuilder.ResolveDpi(document.Settings);
        var encoded = document.Pages
            .Select(page => PdfDocumentBuilder.EncodePage(page, options, encoder, dpi))
            .ToList();
        byte[] pdf = PdfDocumentBuilder.Build(
            document, options, encoded, clockwiseQuarterTurns: 0,
            perPageQuarterTurns: perPageTurns ?? new[] { 0, 0, 0 });

        string path = Path.Combine(directory, "scan.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), $"scan-dataset-{Guid.NewGuid():N}");

    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch { /* best effort */ }
    }
}
