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
        if (IsAnnotating)
        {
            // 插页同样让后续页索引平移，按页标注会错位——先退出标注再插。
            ShowTransientStatus("请先退出标注再插入页面");
            return;
        }

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
            await GrowStripAfterInsertAsync(insertAfterIndex, added).ConfigureAwait(true);
            op.Step("插入页就位", $"{slot}新增 {added} 页");
            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return; // pages are fixed in the file; nothing is on screen any more
            }

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
    /// 「插入文件」：把另一个 PDF 的页插到当前页之后，一次写盘。与插入扫描页共用同一套收尾
    /// （安全写盘、新页方向校正、条带增量刷新），区别只在来源是用户选的文件、且用完后原地保留。
    /// </summary>
    internal async Task InsertFileAfterAsync(int insertAfterIndex, string sourcePdfPath)
    {
        if (IsCancelled || IsRotating || IsOrienting) return;
        if (insertAfterIndex < -1 || insertAfterIndex >= PageCount) return;

        if (string.IsNullOrWhiteSpace(sourcePdfPath) || !File.Exists(sourcePdfPath))
        {
            StatusText = "要插入的 PDF 不存在。";
            return;
        }

        // 把文件插进它自己会得到页数翻倍的结果，且写盘时读写的是同一个文件 —— 直接拒绝。
        if (string.Equals(
                Path.GetFullPath(sourcePdfPath), Path.GetFullPath(_file.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "不能把文件插入它自己。";
            return;
        }

        string slot = insertAfterIndex < 0 ? "开头" : $"第 {insertAfterIndex + 1} 页后";
        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "插入文件页", $"{slot} / {Path.GetFileName(sourcePdfPath)}");

        IsRotating = true;
        StatusText = $"正在插入 {Path.GetFileName(sourcePdfPath)}…";
        try
        {
            PdfInsertResult insert = await _edits
                .InsertAsync(insertAfterIndex, sourcePdfPath)
                .ConfigureAwait(true);
            int added = insert.AddedPages;
            if (added == 0)
            {
                StatusText = $"{Path.GetFileName(sourcePdfPath)} 里没有可插入的页。";
                op.Complete("源文件无页面，未插入");
                return;
            }

            op.Step("插入写盘", $"{slot}新增 {added} 页" +
                (insert.SavedAsCopy ? $"（另存为 {Path.GetFileName(insert.PdfPath)}）" : string.Empty));
            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return;
            }

            await GrowStripAfterInsertAsync(insertAfterIndex, added).ConfigureAwait(true);
            op.Step("插入页就位", $"新增 {added} 页");
            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return;
            }

            await RenderAsync(Math.Min(insertAfterIndex + 1, PageCount - 1), force: true).ConfigureAwait(true);
            op.Step("画布刷新", $"第 {PageIndex + 1} 页");

            if (StatusText.Length == 0)
            {
                StatusText = $"已从 {Path.GetFileName(sourcePdfPath)} 插入 {added} 页" +
                    (insert.SavedAsCopy
                        ? $"，原文件被其他程序占用，已另存为 {Path.GetFileName(insert.PdfPath)}"
                        : "。");
            }
            op.Complete($"{slot}新增 {added} 页，共 {PageCount} 页");
        }
        catch (FileNotFoundException ex)
        {
            op.Fail($"插入文件失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            op.Fail($"插入文件失败：{Path.GetFileName(sourcePdfPath)}", ex);
            if (!IsCancelled) StatusText = $"插入文件失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>页面按钮的入口：插到当前显示页之后。</summary>
    internal void InsertFileAfterCurrent(string sourcePdfPath) =>
        _ = InsertFileAfterAsync(PageIndex, sourcePdfPath);

    /// <summary>
    /// 插入写盘后的公共收尾：渲染缓存按插入点位移、页数上调、新页做一次方向校正，再在条带里
    /// 原地插入缩略图（插入点之后的瓦片重新编号，只有新页需要像素）。插入扫描页与插入文件页
    /// 共用这一段，保证两种来源的插入在界面上完全一致。
    /// </summary>
    private async Task GrowStripAfterInsertAsync(int insertAfterIndex, int added)
    {
        _cache.ShiftPages(insertAfterIndex, added);
        PageCount += added;

        // 新页可能横躺或倒置，与文档原来的方向无关：逐页投票，歪的一次性转正，再让条带绘制。
        await OrientInsertedPagesAsync(insertAfterIndex + 1, added).ConfigureAwait(true);
        _file.RememberStamp(force: true);

        // Grow the strip in place: tiles before the gap keep their page, tiles after it are
        // re-pointed with a notification, and only the inserted pages need pixels. The old
        // clear-and-rebuild threw away every tile and every cached render, so the whole
        // strip blanked and repainted page by page — the post-insert jolt.
        int firstMoved = insertAfterIndex + 1;
        for (int i = firstMoved; i < Thumbnails.Count; i++)
            Thumbnails[i].SetIndex(i + added);

        var fresh = new List<PdfPreviewThumbnailViewModel>(added);
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
