using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
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
            // A folded strip keeps its tiles (cheap, visible again on unfold) but gives back the
            // full-page PNG cache: those are multi-megabyte LOH objects nothing is showing.
            if (!job.StripOpen) job.Strip.TrimRenderCache();
            AppLog.Info(job.StripOpen ? $"打开预览：{job.OutputPath}" : "收起预览");
            return;
        }

        Open(job);
    }

    /// <summary>Builds and unfolds the row's strip immediately. Scans call this the moment the
    /// PDF lands (zero-click preview); other sections reach it through the preview button.</summary>
    public void Open(FileJobViewModel job)
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
            scanProvider: scanRow ? _scanIntoStrip : null); // scan rows can insert scans into gaps
        strip.EnlargeRequested += (_, _) => EnlargeRequested(strip);
        job.AttachStrip(strip);
        // Folded strips are the ones nobody is looking at: hand their full-page byte caches back
        // so a long session doesn't keep every page of every document resident.
        foreach (var other in _jobs)
            if (!ReferenceEquals(other, job) && !other.StripOpen)
                other.Strip?.TrimRenderCache();
        AppLog.Info($"打开预览：{path}");
        _ = strip.LoadAsync();
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
