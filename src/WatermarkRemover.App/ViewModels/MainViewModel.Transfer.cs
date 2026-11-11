using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Presentation;
using WatermarkRemover.Pdf.Rendering;
using WatermarkRemover.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Index;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class MainViewModel
{
    // --- 手机传文件 ---------------------------------------------------

    /// <summary>The 手机传文件 page's state (start/stop, QR, receive tally); wired to the shell.</summary>
    public TransferPanelViewModel Transfer { get; }

    /// <summary>快速搜索 page state (query, hits, open/reveal/copy actions).</summary>
    public SearchPanelViewModel Search { get; }

    /// <summary>Socket thread → UI thread: each landed phone file becomes a finished record in
    /// the 手机传文件 section, so rename, suffix chips, delete, upload and open-folder all work
    /// unchanged.</summary>
    private void OnTransferFileReceived(string path, long bytes)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) _ = AddReceivedFileAsync(path, bytes);
        else dispatcher.Invoke(() => _ = AddReceivedFileAsync(path, bytes));
    }

    /// <summary>
    /// Received files get the same content naming as scans before their row appears: a phone photo
    /// of a document lands under its recognised name, not 「IMG_2031.jpg」. Naming never blocks the
    /// record — any failure (or an unsupported type) keeps the name it arrived with.
    /// </summary>
    private async Task AddReceivedFileAsync(string path, long? receivedBytes)
    {
        string finalPath = path;
        try
        {
            finalPath = await _naming.TryRenameReceivedAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Trace("手机传文件：自动命名异常，保留原文件名", ex);
        }

        long bytes = receivedBytes ?? 0;
        try { bytes = new FileInfo(finalPath).Length; } catch { /* best effort — size display only */ }
        AddTransferJob(finalPath, bytes);
    }

    private void AddTransferJob(string path, long bytes)
    {
        string size = bytes switch
        {
            >= 1L << 30 => $"{bytes / 1073741824.0:F2} GB",
            >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
                _ => $"{Math.Max(1, bytes / 1024)} KB",
        };
        var job = new FileJobViewModel(path, Settings.QuickSuffixes)
        {
            OwnerFeatureId = "transfer.page",
        };
        WireJob(job);
        job.Apply(FeatureResult.Success(path, path, $"来自手机 · {size}", 1));
        Board.Add(job);
        Board.MoveToTop(job);
        // 手机传文件 is its own entry with no queue behind it: unfold this new record's strip here
        // so a completed transfer reads exactly like a completed scan or drop.
        _preview.OpenWhenFinished(job);
    }

    /// <summary>Worker thread → UI thread: a file composed from a CamScanner share joins the
    /// list as a finished record. General packs go through content naming like scans do; the
    /// 身份证 template is already the final printable sheet and skips OCR naming.</summary>
    private void OnTransferShareDocumentReceived(string path, bool runContentNaming)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (!runContentNaming)
        {
            dispatcher.Invoke(() =>
            {
                long bytes = 0;
                try { bytes = new FileInfo(path).Length; } catch { /* best effort — size display only */ }
                AddTransferJob(path, bytes);
            });
            return;
        }

        _ = dispatcher.InvokeAsync(() => AddReceivedFileAsync(path, null));
    }

    /// <summary>Stops the LAN receiver; the window calls this on close so the port frees at once.</summary>
    public void Shutdown()
    {
        // Last chance to sync dataset truth with the PDFs the previews may have edited.
        _preview.FlushAllDatasets();
        Transfer.Stop();
        _outputWatcher?.Dispose();
    }

    // --- 聊天 → PDF 处理页 的往返 ------------------------------------------

    /// <summary>The PDF processor a share-link document is handed to.</summary>
    private const string PdfProcessorFeatureId = "watermark.badge";

    /// <summary>Title-row button on the PDF page: back to the 手机传文件 chat.</summary>
    public RelayCommand ReturnToTransferPageCommand { get; }

    private bool _canReturnToTransfer;

    /// <summary>
    /// True while the PDF page was entered from the 手机传文件 chat: the shell header then
    /// offers 「返回手机传文件」 so the round trip never depends on the sidebar.
    /// </summary>
    public bool CanReturnToTransfer
    {
        get => _canReturnToTransfer;
        private set => SetProperty(ref _canReturnToTransfer, value);
    }

    /// <summary>
    /// Chat → PDF page: switches to the PDF processor and loads the link's document (when one
    /// exists) so the user lands on a page that already has something to work on.
    /// </summary>
    private void OnTransferPdfPageRequested(string? pdfPath)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.Invoke(() =>
        {
            IFeature? target = NavItems
                .Select(item => item.Feature)
                .FirstOrDefault(feature => feature.Id == PdfProcessorFeatureId);
            if (target is null) return;

            CanReturnToTransfer = true;
            SelectedFeature = target;

            if (pdfPath is { } path && File.Exists(path)) _ = AddFilesAsync(new[] { path });
            else AppLog.Info("手机传文件：切换到 PDF 处理页（无待加载文件）");
        });
    }

    /// <summary>PDF page → chat: the header button's counterpart.</summary>
    public void ReturnToTransferPage()
    {
        IFeature? transfer = NavItems
            .Select(item => item.Feature)
            .FirstOrDefault(feature => feature.Id == "transfer.page");
        if (transfer is null) return;

        CanReturnToTransfer = false;
        SelectedFeature = transfer;
    }
}
