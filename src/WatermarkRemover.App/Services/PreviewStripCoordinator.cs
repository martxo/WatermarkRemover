using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Pdf.Rendering;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Owns the thumbnail strip lifecycle: one strip view model per row, reused for every later open,
/// so the row, its strip and the enlarge overlay all drive one shared instance — one render
/// cache, one rotation lock, one truth. The host supplies the dialog, renderer, OCR engine and
/// the scanner callback; it also owns the viewer window through <see cref="ViewerOpener"/>.
/// </summary>
public sealed class PreviewStripCoordinator
{
    private readonly ObservableCollection<FileJobViewModel> _jobs;
    private readonly IDialogService _dialogs;
    private readonly IPdfPageRenderer _renderer;
    private readonly IOcrEngine _ocr;
    private readonly Func<IProgress<string>, Task<string?>>? _scanIntoStrip;

    public PreviewStripCoordinator(
        ObservableCollection<FileJobViewModel> jobs,
        IDialogService dialogs,
        IPdfPageRenderer renderer,
        IOcrEngine ocr,
        Func<IProgress<string>, Task<string?>>? scanIntoStrip)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _scanIntoStrip = scanIntoStrip;
    }

    /// <summary>Opens the screen-level viewer for a strip; supplied by the host so no window
    /// type leaks into this service.</summary>
    public Action<PdfPreviewViewModel>? ViewerOpener { get; set; }

    /// <summary>
    /// Puts the capture-order live thumbnails into PDF page order. Blank pages are dropped and a
    /// duplex scan re-orders the backs, so indexing <c>LiveThumbnails</c> by page number would put
    /// page A's picture on page B — and with it page A's rotation. When the map does not line up
    /// the caller's list is returned untouched: falling back to rendering the PDF is better than
    /// pasting the wrong page.
    /// </summary>
    internal static IReadOnlyList<ImageSource> AlignSeedsToPdf(
        IReadOnlyList<ImageSource> live, IReadOnlyList<int>? sourcePageNumbers, int pageCount)
    {
        if (sourcePageNumbers is not { Count: > 0 } numbers ||
            numbers.Count != pageCount || live.Count == 0)
            return live;

        var seeds = new ImageSource[numbers.Count];
        for (int i = 0; i < numbers.Count; i++)
        {
            int index = numbers[i] - 1;
            if (index < 0 || index >= live.Count) return live;
            seeds[i] = live[index];
        }
        return seeds;
    }

    /// <summary>Expands (or folds) the thumbnail strip under a finished record.</summary>
    public void Toggle(FileJobViewModel? job)
    {
        if (job is null) return;

        // Built already: fold/unfold purely from state — the cached tiles keep working even if
        // the file vanished externally, and a strip must always be foldable again.
        if (job.Strip is not null)
        {
            job.StripOpen = !job.StripOpen;
            if (job.StripOpen)
            {
                ReleaseOtherStrips(job);
                if (!job.Strip.HasThumbnailImages) job.Strip.ReloadThumbnailImages();
            }
            else
            {
                // Folded strips hand back both the page cache and the decoded tile bitmaps: a
                // long session otherwise keeps every row's thumbnails resident.
                job.Strip.FlushDatasetTruth("preview-fold");
                job.Strip.ReleaseThumbnailImages();
            }
            AppLog.Info(job.StripOpen ? $"打开预览：{job.OutputPath}" : "收起预览");
            return;
        }

        Open(job);
    }

    /// <summary>
    /// Builds the row's strip and unfolds it — the record that just finished is the one the user
    /// wants to look at, so no extra click is needed; every other row folds via
    /// <see cref="ReleaseOtherStrips"/>. <paramref name="seedThumbnails"/> are the pages already
    /// shown live during the scan: the strip starts with them so it never flashes an empty grid
    /// and then a second set of tiles. <paramref name="skipScanPostProcess"/> marks a scan whose
    /// orientation was settled and whose blank pages were filtered at capture time (v1.16.83):
    /// the strip's automatic orient/blank passes would only repeat that work, so they stay off
    /// (the manual buttons remain).
    /// </summary>
    public void Open(
        FileJobViewModel job,
        IReadOnlyList<ImageSource>? seedThumbnails = null,
        bool skipScanPostProcess = false,
        IReadOnlyList<int>? undecidedPageIndices = null,
        int decidedPages = 0,
        IReadOnlyList<int>? halfTurnedPageIndices = null,
        string? datasetDirectory = null,
        IReadOnlyList<int>? pageTurns = null)
    {
        string? path = job.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Info("这条记录没有可预览的 PDF 文件。", "提示");
            return;
        }

        // Scan rows (this method's zero-click entry) self-correct their orientation after the
        // preview loads; files opened via the preview button keep the manual button only.
        bool scanRow = job.OwnerFeatureId == "scan.page";
        // A PDF dropped onto the scan page is somebody's existing document: the automatic
        // orientation vote and blank-page removal are scan-capture conveniences and would
        // silently rewrite a file the user only wanted to edit.
        bool autoPostProcess = scanRow && !skipScanPostProcess && !job.SuppressAutoPostProcess;
        var strip = new PdfPreviewViewModel(
            path, job.DisplayName, _renderer,
            confirmPageDelete: index => _dialogs.Confirm(
                $"确定要删除第 {index + 1} 页吗？\n\n该页将从 PDF 中移除并立即保存，删除后无法恢复。",
                "删除确认"),
            ocrEngine: _ocr,
            autoOrient: autoPostProcess,
            onRelocated: job.UpdateRenamed, // a renamed file is tracked, the row follows
            autoCleanBlank: autoPostProcess, // duplex scans drop their blank back sides by themselves
            scanProvider: scanRow ? _scanIntoStrip : null, // scan rows can insert scans into gaps
            seedThumbnails: seedThumbnails,
            undecidedPageIndices: undecidedPageIndices,
            decidedPages: decidedPages,
            halfTurnedPageIndices: halfTurnedPageIndices,
            pageTurns: pageTurns,
            chooseBatchUpright: count => BatchUprightDialog.Show(Application.Current?.MainWindow, count),
            datasetDirectory: datasetDirectory,
            dialogs: _dialogs);
        strip.EnlargeRequested += (_, _) => EnlargeRequested(strip);
        job.AttachStrip(strip);
        // Accordion: the row being opened is the only one whose decoded tiles stay resident.
        // Older strips fold and release their bitmaps, so a scan-heavy session holds one
        // document's pixels instead of every document's.
        ReleaseOtherStrips(job);
        AppLog.Info($"打开预览：{path}");
        _ = LoadAsync(strip, job);
    }

    /// <summary>
    /// Runs the strip's post-load work (auto-orient / blank cleanup). A row folded while its
    /// load was still in flight hands the decoded tiles back instead of keeping them resident.
    /// </summary>
    private static async Task LoadAsync(PdfPreviewViewModel strip, FileJobViewModel job)
    {
        try
        {
            await strip.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"预览加载失败：{ex.Message}");
            return;
        }

        if (!job.StripOpen)
        {
            strip.ReleaseThumbnailImages();
        }
    }

    /// <summary>Folds and releases every strip except <paramref name="keep"/>.</summary>
    private void ReleaseOtherStrips(FileJobViewModel keep)
    {
        foreach (var other in _jobs)
        {
            if (ReferenceEquals(other, keep)) continue;
            if (other.StripOpen)
            {
                other.Strip?.FlushDatasetTruth("preview-fold");
                other.StripOpen = false;
            }
            other.Strip?.ReleaseThumbnailImages();
        }
    }

    /// <summary>App-exit flush: every open strip writes the final on-disk /Rotate back to its
    /// dataset truth before the process ends. Best-effort, never throws.</summary>
    public void FlushAllDatasets()
    {
        foreach (var job in _jobs)
            job.Strip?.FlushDatasetTruth("app-exit");
    }

    /// <summary>
    /// Zero-click preview for every entry that just finished (扫描 / 拖入文件 / 手机传文件 …):
    /// the new record unfolds its strip while all other rows stay folded. Rows whose output is
    /// not a PDF on disk (images and other formats) are skipped silently — no dialog, no strip.
    /// Called from queue workers and transfer callbacks, so the build marshals to the UI thread.
    /// </summary>
    public void OpenWhenFinished(FileJobViewModel job)
    {
        if (job.State != JobState.Succeeded) return;
        if (job.Strip is not null) return; // scan rows already opened their own strip
        if (!job.ShowPreview || job.OutputPath is not { } output || !File.Exists(output)) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) Open(job);
        // Wrap in a zero-argument lambda on purpose: the method group's natural delegate type is
        // Open(job, seedThumbnails, skipScanPostProcess, …) — ALL parameters — so
        // Dispatcher.Invoke(Open, job) handed DynamicInvoke one argument for a ten-argument
        // delegate and threw TargetParameterCountException on the UI thread the moment a job
        // finished on a worker thread (a crash dialog right after every successful run).
        else dispatcher.Invoke(() => Open(job));
    }

    /// <summary>A tile was clicked: the host opens the screen-level viewer on this strip.</summary>
    private void EnlargeRequested(PdfPreviewViewModel page)
    {
        AppLog.Info($"打开看图模式：{page.Title}");
        ViewerOpener?.Invoke(page);
    }
}
