using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
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
                job.Strip.ReleaseThumbnailImages();
            }
            AppLog.Info(job.StripOpen ? $"打开预览：{job.OutputPath}" : "收起预览");
            return;
        }

        Open(job);
    }

    /// <summary>
    /// Builds the row's strip. Scans pass <paramref name="autoOpen"/>=false: the post-load work
    /// still runs, but the row stays folded until the user presses 预览. Other sections open it
    /// through the preview button (autoOpen defaults to true).
    /// <paramref name="seedThumbnails"/> are the pages already shown live during the scan: the
    /// strip starts with them so it never flashes an empty grid and then a second set of tiles.
    /// </summary>
    public void Open(
        FileJobViewModel job,
        IReadOnlyList<ImageSource>? seedThumbnails = null,
        bool autoOpen = true)
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
        var strip = new PdfPreviewViewModel(
            path, Path.GetFileName(path), _renderer,
            confirmPageDelete: index => _dialogs.Confirm(
                $"确定要删除第 {index + 1} 页吗？\n\n该页将从 PDF 中移除并立即保存，删除后无法恢复。",
                "删除确认"),
            ocrEngine: _ocr,
            autoOrient: scanRow,
            onRelocated: job.UpdateRenamed, // a renamed file is tracked, the row follows
            autoCleanBlank: scanRow, // duplex scans drop their blank back sides by themselves
            scanProvider: scanRow ? _scanIntoStrip : null, // scan rows can insert scans into gaps
            seedThumbnails: seedThumbnails);
        strip.EnlargeRequested += (_, _) => EnlargeRequested(strip);
        job.AttachStrip(strip, autoOpen);
        // Accordion: the row being opened is the only one whose decoded tiles stay resident.
        // Older strips fold and release their bitmaps, so a scan-heavy session holds one
        // document's pixels instead of every document's.
        ReleaseOtherStrips(job);
        AppLog.Info($"打开预览：{path}");
        _ = LoadAndMaybeFoldAsync(strip, job, autoOpen);
    }

    /// <summary>
    /// Runs the strip's post-load work (auto-orient / blank cleanup) even when it stays folded:
    /// scans keep their corrected output, but a folded row releases every decoded tile once the
    /// load finishes instead of holding the document's pixels in the history list.
    /// </summary>
    private static async Task LoadAndMaybeFoldAsync(
        PdfPreviewViewModel strip, FileJobViewModel job, bool autoOpen)
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

        if (!autoOpen && !job.StripOpen)
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
            if (other.StripOpen) other.StripOpen = false;
            other.Strip?.ReleaseThumbnailImages();
        }
    }

    /// <summary>
    /// Zero-click preview for drag-dropped rows: the moment a watermark job lands its PDF, the
    /// strip unfolds exactly like a fresh scan's. Runs on the queue's worker thread, so the strip
    /// build marshals to the UI thread. Scan rows are excluded — their strip already opened when
    /// the scan landed, and a second call would fold it back up.
    /// </summary>
    public void OpenWhenFinished(FileJobViewModel job)
    {
        if (job.OwnerFeatureId != "watermark.badge") return;
        if (job.State != JobState.Succeeded) return;
        if (job.Strip is not null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) Open(job);
        else dispatcher.Invoke(Open, job);
    }

    /// <summary>A tile was clicked: the host opens the screen-level viewer on this strip.</summary>
    private void EnlargeRequested(PdfPreviewViewModel page)
    {
        AppLog.Info($"打开看图模式：{page.Title}");
        ViewerOpener?.Invoke(page);
    }
}
