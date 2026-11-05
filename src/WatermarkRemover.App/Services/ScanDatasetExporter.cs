using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Pdf;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.App.Services;

/// <summary>
/// One page's training record, in final PDF reading order. <see cref="ProbePng"/> is the exact
/// byte array the production pipeline fed to doc_ori — never a re-render.
/// </summary>
internal sealed record ScanDatasetPage(
    int PageIndex,
    int CapturePageNumber,
    byte[] ProbePng,
    int? ProbeTurns,
    float ProbeConfidence,
    bool? BaselineHorizontal,
    bool LandscapeGuard,
    int FinalRotate,
    string Source);

/// <summary>Session-level metadata written to <c>session.txt</c>.</summary>
internal sealed record ScanDatasetSession(
    string ScannerModel,
    string Resolution,
    string Duplex,
    string Binding,
    int PageCount,
    int BlankPageCount,
    long ScanDurationMs);

/// <summary>
/// Writes the opt-in scan dataset used to fine-tune the page-orientation model later. One
/// directory per scan session under <c>Documents\WatermarkRemover-Dataset</c>; local only,
/// never uploaded. The switch is off by default and can also be forced with the environment
/// variable <c>WMR_DATASET_EXPORT=1</c>.
///
/// Every write is best-effort: an export failure must never affect a scan, so all of them
/// swallow exceptions and only leave a Debug line.
/// </summary>
internal sealed class ScanDatasetExporter
{
    private const string RootFolderName = "WatermarkRemover-Dataset";

    private ScanDatasetExporter(string sessionDirectory) => SessionDirectory = sessionDirectory;

    public string SessionDirectory { get; }

    /// <summary>
    /// Null when the switch is off. Otherwise creates the session directory and logs its path
    /// exactly once, so a support log shows where the data went.
    /// </summary>
    public static ScanDatasetExporter? Create(
        AppSettings settings,
        string? rootOverride = null,
        string? envOverride = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? env = envOverride ?? Environment.GetEnvironmentVariable("WMR_DATASET_EXPORT");
        bool enabled = settings.ScanDatasetExportEnabled || IsTruthy(env);
        if (!enabled) return null;

        try
        {
            string root = rootOverride ?? DefaultRoot();
            Directory.CreateDirectory(root);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string session = Path.Combine(root, stamp);
            Directory.CreateDirectory(session);
            AppLog.Info($"扫描训练数据导出已开启，本份目录：{session}（仅本地保存，不会上传）");
            return new ScanDatasetExporter(session);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"扫描训练数据导出目录创建失败，本份跳过导出：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Capture-time frame export. Written under a capture-order name first because the final
    /// page order (duplex interleave) is only known after assembly; <see cref="Complete"/> renames.
    /// </summary>
    public void SaveCaptureFrame(int capturePageNumber, RasterImage frame)
    {
        try
        {
            string path = Path.Combine(SessionDirectory, $"c{capturePageNumber}-frame.png");
            byte[] png = EncodeGrayPng(frame);
            File.WriteAllBytes(path, png);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"扫描训练数据：第 {capturePageNumber} 页原始帧导出失败（{ex.Message}）");
        }
    }

    /// <summary>
    /// Writes the per-page records, session metadata and the on-disk /Rotate truth. Frames are
    /// renamed from capture order to final page order; frames of dropped pages are removed.
    /// </summary>
    public void Complete(
        IReadOnlyList<ScanDatasetPage> pages,
        ScanDatasetSession session,
        string pdfPath)
    {
        try
        {
            foreach (ScanDatasetPage page in pages)
            {
                string captureFrame = Path.Combine(
                    SessionDirectory, $"c{page.CapturePageNumber}-frame.png");
                string finalFrame = Path.Combine(
                    SessionDirectory, $"p{page.PageIndex}-frame.png");
                if (File.Exists(captureFrame))
                    File.Move(captureFrame, finalFrame, overwrite: true);

                // The probe is the production byte array, written as-is (same source bytes).
                File.WriteAllBytes(
                    Path.Combine(SessionDirectory, $"p{page.PageIndex}-probe.png"), page.ProbePng);

                File.WriteAllText(
                    Path.Combine(SessionDirectory, $"p{page.PageIndex}.txt"), FormatPage(page));
            }

            // Frames for pages that were dropped (blank / encode failure) have no final record.
            foreach (string stale in Directory.EnumerateFiles(SessionDirectory, "c*-frame.png"))
            {
                try { File.Delete(stale); } catch (IOException) { /* best effort */ }
            }

            File.WriteAllText(Path.Combine(SessionDirectory, "session.txt"), FormatSession(session));
            File.WriteAllText(Path.Combine(SessionDirectory, "truth.txt"), FormatTruth(pdfPath));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"扫描训练数据收尾失败（数据可能不完整）：{ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites <c>truth.txt</c> from the PDF that is on disk now and appends a note with the
    /// refresh time and reason. Called after every preview page rotation / batch upright, and
    /// again when the preview closes or the app exits, so the label tracks the final file rather
    /// than the scan-time snapshot. Best-effort: never throws into the caller.
    /// </summary>
    public static void RefreshTruth(string? sessionDirectory, string pdfPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) ||
            !Directory.Exists(sessionDirectory))
            return;

        try
        {
            IReadOnlyList<int> rotations = PdfPageRotationReader.Read(pdfPath);
            File.WriteAllText(
                Path.Combine(sessionDirectory, "truth.txt"), FormatTruth(rotations));

            string stamp = DateTimeOffset.Now.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            string notePath = Path.Combine(sessionDirectory, "truth-refresh.txt");
            if (!File.Exists(notePath))
                File.AppendAllText(notePath, "# time,reason,pageCount" + Environment.NewLine);
            File.AppendAllText(
                notePath,
                $"{stamp},{reason},{rotations.Count}" + Environment.NewLine);

            AppLog.Info($"训练数据 truth 已刷新：{sessionDirectory}（{reason}，{rotations.Count} 页）");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"训练数据 truth 刷新失败（{reason}）：{ex.Message}");
        }
    }

    /// <summary>
    /// Appends one user correction to <c>manual-rotations.txt</c>: time, 1-based page, clockwise
    /// degrees, source (Manual / Batch). Probe and frame stay untouched; this is the audit trail
    /// that lets later processing prefer the human-corrected angle and mine the error.
    /// </summary>
    public static void AppendManualRotation(
        string? sessionDirectory, int pageIndex, int clockwiseDegrees, string source)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) ||
            !Directory.Exists(sessionDirectory))
            return;

        try
        {
            string stamp = DateTimeOffset.Now.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            string path = Path.Combine(sessionDirectory, "manual-rotations.txt");
            if (!File.Exists(path))
                File.AppendAllText(path, "# time,pageIndex,degrees,source" + Environment.NewLine);
            File.AppendAllText(
                path,
                $"{stamp},{pageIndex},{clockwiseDegrees},{source}" + Environment.NewLine);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"训练数据手动旋转记录失败：{ex.Message}");
        }
    }

    private static string FormatPage(ScanDatasetPage page)
    {
        string baseline = page.BaselineHorizontal is null
            ? "-"
            : page.BaselineHorizontal.Value ? "H" : "V";
        string turns = page.ProbeTurns is null
            ? "-"
            : page.ProbeTurns.Value.ToString(CultureInfo.InvariantCulture);
        string confidence = page.ProbeConfidence.ToString("0.000", CultureInfo.InvariantCulture);
        return "# pageIndex,probeTurns,probeConf,baseline,landscapeGuard,finalRotate,source" +
               Environment.NewLine +
               string.Join(",",
                   page.PageIndex.ToString(CultureInfo.InvariantCulture),
                   turns,
                   confidence,
                   baseline,
                   page.LandscapeGuard ? "1" : "0",
                   page.FinalRotate.ToString(CultureInfo.InvariantCulture),
                   page.Source) +
               Environment.NewLine;
    }

    private static string FormatSession(ScanDatasetSession session) =>
        "scanner=" + session.ScannerModel + Environment.NewLine +
        "resolution=" + session.Resolution + Environment.NewLine +
        "duplex=" + session.Duplex + Environment.NewLine +
        "binding=" + session.Binding + Environment.NewLine +
        "pages=" + session.PageCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
        "blankPages=" + session.BlankPageCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
        "scanMs=" + session.ScanDurationMs.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;

    /// <summary>Truth is read back from the written file — never the in-memory turn list.</summary>
    private static string FormatTruth(string pdfPath) =>
        FormatTruth(PdfPageRotationReader.Read(pdfPath));

    private static string FormatTruth(IReadOnlyList<int> rotations)
    {
        var lines = new List<string> { "# pageIndex,rotate" };
        for (var index = 0; index < rotations.Count; index++)
            lines.Add($"{index + 1},{rotations[index]}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>BGRA raw frame → Gray8 PNG, keeping the scanner's original pixel dimensions.</summary>
    internal static byte[] EncodeGrayPng(RasterImage frame)
    {
        var sourceInfo = new SKImageInfo(
            frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var source = new SKBitmap(sourceInfo);
        Marshal.Copy(frame.Bgra, 0, source.GetPixels(), frame.Bgra.Length);

        var grayInfo = new SKImageInfo(
            frame.Width, frame.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var gray = new SKBitmap(grayInfo);
        using var sourcePixels = source.PeekPixels();
        if (sourcePixels is null ||
            !sourcePixels.ReadPixels(grayInfo, gray.GetPixels(), gray.RowBytes))
            throw new InvalidOperationException("无法把原始帧转换为灰度位图。");

        using var image = SKImage.FromBitmap(gray);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("无法编码灰度 PNG。");
        return data.ToArray();
    }

    private static string DefaultRoot()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents.Length > 0 ? documents : AppFolders.Root, RootFolderName);
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Trim() == "1" ||
         string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
}
