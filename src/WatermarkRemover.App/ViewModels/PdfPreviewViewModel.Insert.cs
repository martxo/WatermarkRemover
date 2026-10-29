using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class PdfPreviewViewModel
{
    /// <summary>
    /// Gap button between two pages: one scan pass (the host supplies the scanner and reports
    /// progress through <see cref="StatusText"/>), then the scanned pages are spliced into the
    /// PDF right after the page the gap follows — one in-place save, then the strip and the
    /// canvas rebuild onto the new arrangement with the first inserted page on screen. The
    /// scan's intermediate file is removed once its pages live inside the target; a failed
    /// scan or a failed insert keeps it, so nothing is ever lost.
    /// </summary>
    internal async Task InsertScanAfterAsync(int insertAfterIndex)
    {
        if (IsCancelled || IsRotating || IsOrienting) return;
        if (_scanProvider is null) return;
        if (insertAfterIndex < -1 || insertAfterIndex >= PageCount) return; // -1 = in front of page one

        string slot = insertAfterIndex < 0 ? "开头" : $"第 {insertAfterIndex + 1} 页后";
        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "插入扫描页", $"{slot} / {Path.GetFileName(_file.Path)}");

        IsRotating = true;
        StatusText = "正在准备扫描…";
        string? scannedPath = null;
        bool inserted = false;
        try
        {
            scannedPath = await _scanProvider(new Progress<string>(message => StatusText = message)).ConfigureAwait(true);
            op.Step("扫描", scannedPath is null ? "未产出文件" : Path.GetFileName(scannedPath));
            if (IsCancelled)
            {
                op.Complete("已取消");
                return;
            }
            if (scannedPath is null || !File.Exists(scannedPath))
            {
                StatusText = "扫描未完成，没有页面可插入。";
                op.Complete("扫描未产出页面，未插入");
                return;
            }

            PdfInsertResult insert = await _edits
                .InsertAsync(insertAfterIndex, scannedPath!)
                .ConfigureAwait(true);
            int added = insert.AddedPages;
            if (insert.SavedAsCopy)
            {
                StatusText = $"原文件正被其他程序占用，已插入并另存为 {Path.GetFileName(insert.PdfPath)}";
            }

            inserted = added > 0;
            op.Step("插入写盘",
                $"{slot}新增 {added} 页" +
                (insert.SavedAsCopy ? $"（另存为 {Path.GetFileName(insert.PdfPath)}）" : string.Empty));

            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return; // the file is saved; nothing is on screen any more
            }
            _cache.ShiftPages(insertAfterIndex, added);
            PageCount += added;

            // Fresh scanner pages can sit sideways or upside down whatever the document looked
            // like before the gap — probe each new page and turn the crooked ones in place,
            // before the strip below paints them.
            await OrientInsertedPagesAsync(insertAfterIndex + 1, added).ConfigureAwait(true);
            op.Step("插入页方向校正");
            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return; // pages are fixed in the file; nothing is on screen any more
            }
            _file.RememberStamp(force: true);

            // Grow the strip in place: tiles before the gap keep their page, tiles after it are
            // re-pointed with a notification, and only the scanner's pages need pixels. The old
            // clear-and-rebuild threw away every tile and every cached render, so the whole
            // strip blanked and repainted page by page — the post-insert jolt.
            int firstMoved = insertAfterIndex + 1;
            for (int i = firstMoved; i < Thumbnails.Count; i++)
                Thumbnails[i].SetIndex(i + added);
            var fresh = new List<PdfPreviewThumbnailViewModel>();
            for (int i = 0; i < added; i++)
            {
                var tile = new PdfPreviewThumbnailViewModel(this, firstMoved + i);
                fresh.Add(tile);
                Thumbnails.Insert(firstMoved + i, tile);
            }
            SyncThumbnailSelection();
            foreach (var thumb in Thumbnails) thumb.RefreshCommands();
            RebuildStripItems();
            _ = LoadThumbnailsAsync(fresh, ++_thumbnailLoadGeneration);
            op.Step("缩略图刷新", $"新增 {added} 页");

            // Land on the first page that came out of the scanner.
            await RenderAsync(Math.Min(insertAfterIndex + 1, PageCount - 1), force: true).ConfigureAwait(true);
            op.Step("画布刷新", $"第 {PageIndex + 1} 页");

            // RenderAsync clears StatusText on success; write our line after that, and leave a
            // render failure's own message standing.
            if (StatusText.Length == 0) StatusText = $"已插入 {added} 页扫描页。";
            op.Complete($"{slot}新增 {added} 页，共 {PageCount} 页");
        }
        catch (FileNotFoundException ex)
        {
            op.Fail($"插入扫描页失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            op.Fail($"插入扫描页失败：{slot}", ex);
            if (!IsCancelled) StatusText = $"插入扫描页失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
            if (inserted && scannedPath is not null && File.Exists(scannedPath))
            {
                try { File.Delete(scannedPath); }
                catch (IOException)
                {
                    // A leftover scan file beats losing the pages; the log already recorded the insert.
                }
            }
        }
    }

    /// <summary>
    /// Grows the strip after a continuation scan the host already appended to the file: the
    /// existing tiles keep their indexes, the new pages get tiles and renders, and the canvas
    /// lands on the first page that just came in. Runs under the same edit lock as an insert so
    /// no tile can start a rotation while the file is being re-read. Quiet on blank backs — the
    /// same scoped orientation pass the gap insert uses.
    /// </summary>
    internal async Task AppendScannedPagesAsync(int added)
    {
        if (added <= 0 || IsCancelled) return;

        IsRotating = true;
        try
        {
            _file.RememberStamp(force: true);
            int firstNew = PageCount;
            PageCount += added;

            var fresh = new List<PdfPreviewThumbnailViewModel>(added);
            for (int i = 0; i < added; i++)
            {
                var tile = new PdfPreviewThumbnailViewModel(this, firstNew + i);
                fresh.Add(tile);
                Thumbnails.Add(tile);
            }
            SyncThumbnailSelection();
            foreach (var thumb in Thumbnails) thumb.RefreshCommands();
            RebuildStripItems();
            // Same generation: the initial fill may still be running for the first pages, and
            // bumping it would abandon their remaining tiles.
            _ = LoadThumbnailsAsync(fresh, _thumbnailLoadGeneration);

            await OrientInsertedPagesAsync(firstNew, added).ConfigureAwait(true);
            await RenderAsync(firstNew, force: true).ConfigureAwait(true);

            if (StatusText.Length == 0) StatusText = $"已续扫 {added} 页，共 {PageCount} 页。";
            AppLog.Info($"续扫页面已并入预览：{Path.GetFileName(_file.Path)}，" +
                        $"新增 {added} 页，共 {PageCount} 页");
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// Auto-orientation scoped to the pages an insert just brought in: each new page is
    /// rendered and voted on by the same OCR quarter-turn detector the load-time orient uses;
    /// every crooked page is collected first, then all turns land in ONE in-place save. Quiet
    /// by contract — a page the engine cannot read (blank back, unusable OCR) stays untouched.
    /// </summary>
    private Task<int> OrientInsertedPagesAsync(int firstIndex, int count) =>
        _postProcessor.OrientPagesAsync(firstIndex, count, PageCount, OrientationDpi);
}
