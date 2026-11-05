using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class PdfPreviewViewModel
{
    /// <summary>
    /// The scan row's after-load pipeline. Serial by construction — both steps rewrite the same
    /// file and must not race. Ordering, though, is deliberate: the direction verdict only needs
    /// page one, so it runs as soon as the canvas is painted. Blank cleanup walks every page and
    /// its renders are exactly the rasters the strip needs, so running it first made the turn
    /// appear to start only after the last thumbnail. A blank first sheet is the one case that
    /// must wait: its verdict is meaningless and the page that should be probed (the first
    /// content page) only exists after the blank pages are dropped.
    /// </summary>
    private async Task RunScanPostProcessAsync()
    {
        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "扫描后处理", Path.GetFileName(_file.Path), slowMs: 0);

        try
        {
            if (!_autoOrient || _ocrEngine is null)
            {
                await CleanBlankPagesAsync();
                op.Complete("仅清理空白页");
                return;
            }

            // 逐页裁决只要还有未定页就不算“已完成”：部分页已决时只兜底那些未定页，避免整份
            // 旋转把已经摆正的页又转歪；全未决时整份 OCR 兜底仍是唯一可用的保守做法。
            bool scopedFallback =
                _undecidedPageIndices.Count > 0 && _decidedPages > 0 && PageCount > 0;
            bool blankFirstPage = _autoCleanBlank && await IsPageBlankAsync(0);
            op.Step("检测首页空白", blankFirstPage ? "空白" : "有内容");

            if (scopedFallback)
            {
                await FallbackUndecidedPagesAsync();
                op.Step("未裁决页兜底");
            }
            else
            {
                if (blankFirstPage)
                {
                    await CleanBlankPagesAsync();
                    op.Step("清理空白页", "首页空白，先清理");
                }

                await AutoOrientAsync();
                op.Step("方向检测");
            }

            if (_autoCleanBlank && !blankFirstPage)
            {
                await CleanBlankPagesAsync();
                op.Step("清理空白页", "方向检测后清理");
            }

            // 扫描后处理也可能转动/删除页；收尾时把 truth 拉回当前 PDF，避免标签停留在扫描瞬间。
            FlushDatasetTruth("scan-post-process");
            op.Complete("扫描后处理结束");
        }
        catch (Exception ex)
        {
            op.Fail("扫描后处理失败", ex);
        }
    }

    /// <summary>
    /// 只对未裁决页做 OCR 方向兜底，一次写盘，已裁决页保持不动。全未决的文档不走这里
    /// （整份旋转更稳）；OCR 读不出方向的页保持原样，留给「批量摆正」人工一次改完。
    /// </summary>
    private async Task FallbackUndecidedPagesAsync()
    {
        if (IsCancelled || _ocrEngine is null || IsOrienting) return;
        if (PageCount <= 0 || _isLoading) return;

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "未裁决页兜底", Path.GetFileName(_file.Path), slowMs: 0);

        IsOrienting = true;
        StatusText = $"正在兜底摆正 {_undecidedPageIndices.Count} 页未裁决页…";
        try
        {
            int turned = await _postProcessor
                .OrientPagesAsync(PageCount, _undecidedPageIndices, OrientationDpi)
                .ConfigureAwait(true);
            if (IsCancelled)
            {
                op.Complete("已取消");
                return;
            }
            if (turned == 0)
            {
                op.Complete("OCR 无法判定，未改动");
                return;
            }

            op.Step("旋转写盘", $"定向摆正 {turned} 页");
            BuildThumbnails();
            StartThumbnailLoad();
            await RenderAsync(PageIndex, force: true);

            if (StatusText.Length == 0)
                ShowTransientStatus($"已兜底摆正 {turned} 页，其余可点「批量摆正」");
            op.Complete($"定向摆正 {turned} 页");
        }
        catch (OperationCanceledException)
        {
            op.Complete("已取消");
        }
        catch (Exception ex)
        {
            op.Fail("未裁决页兜底失败", ex);
            if (!IsCancelled) StatusText = $"兜底摆正失败：{ex.Message}（文件未改动）";
        }
        finally
        {
            IsOrienting = false;
        }
    }

    /// <summary>
    /// Final truth flush for this session: called when the strip folds and at app exit, so any
    /// automatic post-process (blank cleanup, fallback orientation) that changed page order or
    /// rotations after the last tracked action is still reflected in truth.txt.
    /// </summary>
    public void FlushDatasetTruth(string reason)
    {
        if (_datasetDirectory is null) return;
        // A scan is renamed by the naming pass after it lands; the strip still holds the temp
        // name, so the flush must resolve the rename first (the log used to say "找不到文件").
        _file.TryRelocate();
        ScanDatasetExporter.RefreshTruth(_datasetDirectory, _file.Path, reason);
    }

    /// <summary>Host dialog → batch upright; no host dialog or user cancel means no change.</summary>
    private async Task PromptBatchUprightAsync()
    {
        if (_chooseBatchUpright is null || IsOrienting || IsRotating || PageCount <= 0) return;
        BatchUprightChoice? choice = _chooseBatchUpright(_undecidedPageIndices.Count);
        if (choice is null) return;
        await ApplyBatchUprightAsync(choice.QuarterTurns, choice.OnlyUndecided);
    }

    /// <summary>
    /// 一键批量摆正：整份或仅未裁决页，按用户选择的方向一次写盘，然后重建所有表面。
    /// 与逐页裁决保持一致的单写者锁，失败时文件不动并给出原因。
    /// </summary>
    internal async Task ApplyBatchUprightAsync(int quarterTurns, bool onlyUndecided)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0 || IsCancelled || IsRotating || IsOrienting || PageCount <= 0) return;

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "批量摆正", Path.GetFileName(_file.Path), slowMs: 0);

        IsRotating = true;
        StatusText = "正在批量摆正…";
        try
        {
            int[] undecided = _undecidedPageIndices
                .Where(index => index >= 0 && index < PageCount)
                .Distinct()
                .ToArray();
            bool scoped = onlyUndecided && undecided.Length > 0;
            int affected = scoped ? undecided.Length : PageCount;

            if (scoped)
            {
                await _edits.RotatePagesAsync(
                    undecided.Select(index => (Index: index, Steps: turns)).ToArray());
            }
            else
            {
                await _edits.RotateAllAsync(turns);
            }
            op.Step("旋转写盘", $"顺时针 {turns * 90}°，{(scoped ? "仅未裁决页" : "整份")}共 {affected} 页");

            // Batch upright is a manual correction too: one line per affected page, then refresh.
            int[] affectedPages = scoped ? undecided : Enumerable.Range(0, PageCount).ToArray();
            foreach (int index in affectedPages)
                ScanDatasetExporter.AppendManualRotation(
                    _datasetDirectory, index + 1, turns * 90, "Batch");
            ScanDatasetExporter.RefreshTruth(_datasetDirectory, _file.Path, "batch-upright");

            if (IsCancelled)
            {
                op.Complete("已取消");
                return;
            }

            BuildThumbnails();
            StartThumbnailLoad();
            await RenderAsync(PageIndex, force: true);

            if (StatusText.Length == 0)
                ShowTransientStatus($"已批量摆正（顺时针 {turns * 90}°），已保存");
            UprightNoticeText = string.Empty;
            op.Complete($"顺时针 {turns * 90}°，{affected} 页");
        }
        catch (OperationCanceledException)
        {
            op.Complete("已取消");
        }
        catch (Exception ex)
        {
            op.Fail("批量摆正失败", ex);
            if (!IsCancelled) StatusText = $"批量摆正失败：{ex.Message}（文件未改动）";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>Blank verdict for one cached page; an unreadable page counts as content so the
    /// orientation probe (which is silent on unusable input) still gets its chance.</summary>
    private Task<bool> IsPageBlankAsync(int pageIndex) =>
        _postProcessor.IsPageBlankAsync(pageIndex, PreviewDpi);

    /// <summary>
    /// Drops the blank pages a duplex scan produced. Judged from pixels (no OCR), deleted in one
    /// in-place save, and always leaving at least one page — even an all-blank scan keeps its
    /// first sheet rather than turning into nothing. Silent on failure: the scan result must
    /// never be held hostage by a helper step.
    /// </summary>
    internal async Task CleanBlankPagesAsync()
    {
        if (!_autoCleanBlank || _isOrienting || IsRotating || PageCount <= 1) return;

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "清理空白页", Path.GetFileName(_file.Path), slowMs: 0);

        IsRotating = true; // the single-writer lock: tiles' edit buttons idle while pages go
        try
        {
            var progress = new Progress<(int Index, bool Blank)>(p =>
                op.Step($"检测第 {p.Index + 1}/{PageCount} 页", p.Blank ? "空白" : "有内容"));
            var blankPages = await _postProcessor
                .FindBlankPagesAsync(PageCount, PreviewDpi, progress)
                .ConfigureAwait(true);
            if (IsCancelled)
            {
                op.Complete("已取消");
                return;
            }
            if (blankPages.Count == 0)
            {
                op.Complete("未发现空白页");
                return;
            }

            // Blank detection is a heuristic running unattended, so it runs in one in-place save
            // and says how many pages went; no copy of the pre-cleanup document is kept — the
            // user asked for a clean scan, not a backup pile.
            int removed = await _postProcessor
                .DeleteBlankPagesAsync(PageCount, blankPages)
                .ConfigureAwait(true);
            op.Step("删除空白页写盘", $"删除 {removed} 页");
            if (IsCancelled || removed == 0)
            {
                op.Complete("写盘未生效");
                return;
            }

            // Indexes shifted for every page after the first removed one — the whole render
            // cache is keyed by index, so it all goes; the strip rebuilds below anyway.
            PageCount -= removed;
            BuildThumbnails();
            StartThumbnailLoad();
            await RenderAsync(Math.Min(PageIndex, PageCount - 1), force: true);
            StatusText = $"已自动删除 {removed} 页空白页";
            op.Complete($"删除 {removed} 页空白页，剩余 {PageCount} 页");
        }
        catch (OperationCanceledException)
        {
            op.Complete("已取消");
        }
        catch (FileNotFoundException ex)
        {
            op.Fail($"空白页清理中止（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            // A failed cleanup must never obscure the scan result — fold quietly.
            op.Fail("空白页清理失败", ex);
        }
        finally
        {
            IsRotating = false;
        }
    }
    /// <summary>
    /// Tile corner button: turn the page a quarter turn clockwise, save that into the PDF
    /// immediately, then refresh whatever shows the page — the tile, and the canvas when this is
    /// the page on screen. Any failure leaves the previous orientation (the file is swapped only
    /// after a complete save) and reports the reason in <see cref="StatusText"/>.
    /// </summary>
    internal async Task RotatePageAsync(int pageIndex)
    {
        if (IsCancelled || IsRotating) return;
        if (IsAnnotating)
        {
            // 旋转会改变页面光栅，内存里的按页标注会对不上——先退出标注再转。
            ShowTransientStatus("请先退出标注再旋转页面");
            return;
        }

        if (pageIndex < 0 || pageIndex >= PageCount) return;

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "旋转页面", $"第 {pageIndex + 1} 页 / {Path.GetFileName(_file.Path)}");

        IsRotating = true;
        StatusText = $"正在旋转第 {pageIndex + 1} 页并保存…";
        try
        {
            bool savedAsCopy = await _edits.RotateAsync(pageIndex).ConfigureAwait(true);
            op.Step("旋转写盘",
                $"第 {pageIndex + 1} 页，顺时针 90°{(savedAsCopy ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}" : string.Empty)}");

            // 手动旋转立即落到 dataset：追加人工修正轨迹，并把 truth 从当前 PDF 回读刷新。
            ScanDatasetExporter.AppendManualRotation(_datasetDirectory, pageIndex + 1, 90, "Manual");
            ScanDatasetExporter.RefreshTruth(_datasetDirectory, _file.Path, "manual-rotate");

            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return; // the file is saved; nothing is on screen any more
            }

            byte[] thumbBytes = await _cache.GetPageBytesAsync(pageIndex, ThumbnailDpi, priority: true);
            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return;
            }
            if (pageIndex < Thumbnails.Count)
                Thumbnails[pageIndex].SetImage(await Task.Run(() => MakeThumbSource(thumbBytes)));
            op.Step("缩略图刷新", $"第 {pageIndex + 1} 页，{thumbBytes.Length / 1024} KB");

            if (PageIndex == pageIndex)
                await RenderAsync(pageIndex, force: true);
            op.Step("画布刷新", PageIndex == pageIndex ? $"第 {pageIndex + 1} 页" : "未显示该页");

            // Only replace our own progress line; a render failure reported by RenderAsync must stay.
            // RenderAsync empties the line on success, so a blank line is ours to fill too.
            string copyNote = savedAsCopy
                ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}"
                : string.Empty;
            if (StatusText.Length == 0 || StatusText.StartsWith("正在旋转", StringComparison.Ordinal))
                ShowTransientStatus($"第 {pageIndex + 1} 页已旋转，已保存{copyNote}");
            op.Complete($"第 {pageIndex + 1} 页已顺时针旋转{copyNote}");
        }
        catch (FileNotFoundException ex)
        {
            op.Fail($"旋转保存失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            op.Fail($"旋转保存失败：第 {pageIndex + 1} 页", ex);
            if (!IsCancelled) StatusText = $"旋转保存失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// Tile delete button: remove that page from the PDF right away (after the host confirms),
    /// rebuild the strip with the shifted indexes and show the page that took the deleted one's
    /// place. The last remaining page is never deletable — a zero-page PDF is not a valid
    /// document, so the strip keeps it and the command is disabled on that tile.
    /// </summary>
    internal async Task DeletePageAsync(int pageIndex)
    {
        if (IsCancelled || IsRotating) return;
        if (IsAnnotating)
        {
            // 删页会让后面的页索引前移，按页标注整片错位——先退出标注再删。
            ShowTransientStatus("请先退出标注再删除页面");
            return;
        }

        if (pageIndex < 0 || pageIndex >= PageCount) return;
        if (PageCount <= 1) return; // the last page must survive: empty PDFs cannot be opened
        if (_confirmPageDelete is { } confirm && !confirm(pageIndex)) return;

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "删除页面", $"第 {pageIndex + 1} 页 / {Path.GetFileName(_file.Path)}");

        IsRotating = true;
        StatusText = $"正在删除第 {pageIndex + 1} 页…";
        try
        {
            // Cancel any in-flight tile fill: it reads by index, and the indexes are about to
            // shift. Images already decoded travel with their tiles and stay correct.
            int generation = ++_thumbnailLoadGeneration;

            bool savedAsCopy = await _edits.DeleteAsync(pageIndex).ConfigureAwait(true);
            op.Step("删除页写盘",
                $"第 {pageIndex + 1} 页{(savedAsCopy ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}" : string.Empty)}");

            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return; // the file is saved; nothing is on screen any more
            }

            // Only the deleted tile goes: the pages after it keep their pixels and just re-index,
            // so a delete repaints the canvas instead of re-rendering every thumbnail.
            PageCount--;
            Thumbnails.RemoveAt(pageIndex);
            for (int i = pageIndex; i < Thumbnails.Count; i++)
                Thumbnails[i].SetIndex(i);
            RebuildStripItems();
            SyncThumbnailSelection();
            foreach (var thumb in Thumbnails) thumb.RefreshCommands();
            op.Step("移除缩略图", $"剩余 {PageCount} 页");

            // Tiles that never got an image (folded/aborted fill) still need pixels; the rest keep theirs.
            var empty = Thumbnails.Where(tile => tile.Image is null).ToList();
            if (empty.Count > 0)
                _ = LoadThumbnailsAsync(empty, generation);

            // The page that took the deleted one's place becomes the shown page.
            await RenderAsync(Math.Min(pageIndex, PageCount - 1), force: true);
            op.Step("画布刷新", $"第 {PageIndex + 1} 页");
            string copyNote = savedAsCopy
                ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}"
                : string.Empty;
            if (StatusText.Length == 0 || StatusText.StartsWith("正在删除", StringComparison.Ordinal))
                ShowTransientStatus($"第 {pageIndex + 1} 页已删除，剩余 {PageCount} 页{copyNote}");
            op.Complete($"第 {pageIndex + 1} 页已删除，剩余 {PageCount} 页{copyNote}");
        }
        catch (FileNotFoundException ex)
        {
            op.Fail($"删除页失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            op.Fail($"删除页失败：第 {pageIndex + 1} 页", ex);
            if (!IsCancelled) StatusText = $"删除页失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// Drag-and-drop reorder: write the new page order into the PDF in one save, then move the
    /// tile in place (its image travels with it) and show the page where it landed. The whole
    /// strip is locked for the write — dropping onto another tile while a rotation is saving
    /// would race the file swap, exactly like the corner edit buttons.
    /// </summary>
    internal async Task MovePageAsync(int fromIndex, int toIndex)
    {
        if (IsCancelled || IsRotating || IsOrienting) return;
        if (IsAnnotating)
        {
            // 调整顺序同样重排索引——按页标注必须在对齐后重新开始。
            ShowTransientStatus("请先退出标注再调整页面顺序");
            return;
        }

        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= PageCount) return;
        if (toIndex < 0 || toIndex >= PageCount) return;

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "调整页面顺序",
            $"第 {fromIndex + 1} 页 → 第 {toIndex + 1} 页 / {Path.GetFileName(_file.Path)}");

        IsRotating = true;
        StatusText = "正在调整页面顺序…";
        try
        {
            // Cancel any in-flight tile fill: it reads by index, and the indexes are about to
            // change. Already-loaded images travel with their tiles and stay correct.
            int generation = ++_thumbnailLoadGeneration;

            bool savedAsCopy = await _edits.MoveAsync(fromIndex, toIndex).ConfigureAwait(true);
            op.Step("调整顺序写盘",
                $"第 {fromIndex + 1} 页 → 第 {toIndex + 1} 页{(savedAsCopy ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}" : string.Empty)}");

            if (IsCancelled)
            {
                op.Complete("已取消（文件已保存）");
                return; // the file is saved; nothing is on screen any more
            }

            // Render cache is keyed by index, so every entry is stale now.
            Thumbnails.Move(fromIndex, toIndex);
            for (int i = 0; i < Thumbnails.Count; i++)
                Thumbnails[i].SetIndex(i);
            RebuildStripItems();
            SyncThumbnailSelection();
            foreach (var thumb in Thumbnails) thumb.RefreshCommands();
            op.Step("重排缩略图", $"第 {fromIndex + 1} 页 → 第 {toIndex + 1} 页");

            // Only tiles that never got an image need pixels; the moved ones kept theirs.
            var empty = Thumbnails.Where(tile => tile.Image is null).ToList();
            if (empty.Count > 0)
                _ = LoadThumbnailsAsync(empty, generation);

            await RenderAsync(toIndex, force: true).ConfigureAwait(true);
            op.Step("画布刷新", $"第 {PageIndex + 1} 页");
            string copyNote = savedAsCopy
                ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}"
                : string.Empty;
            if (StatusText.Length == 0 || StatusText.StartsWith("正在调整", StringComparison.Ordinal))
                ShowTransientStatus($"已把第 {fromIndex + 1} 页移到第 {toIndex + 1} 页{copyNote}");
            op.Complete($"第 {fromIndex + 1} 页 → 第 {toIndex + 1} 页，共 {PageCount} 页{copyNote}");
        }
        catch (FileNotFoundException ex)
        {
            op.Fail($"调整顺序失败（文件不可达）：{ex.FileName}", ex);
            if (!IsCancelled) StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            op.Fail($"调整顺序失败：第 {fromIndex + 1} 页 → 第 {toIndex + 1} 页", ex);
            if (!IsCancelled) StatusText = $"调整页面顺序失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// Waits until no load / orientation / page-write / thumbnail-read pass is touching the file.
    /// The host's background naming (a rename = File.Move) races those readers otherwise — a move
    /// during a strip rewrite or thumbnail render throws and the scan keeps its timestamp name
    /// even though the right name was already recognised.
    /// </summary>
    internal async Task WaitForSettledAsync()
    {
        for (int i = 0; i < 300 &&
            (_isLoading || _isOrienting || IsRotating || _thumbnailFillCount > 0); i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// Auto-orientation: render the first page, let the OCR engine vote on the four quarter
    /// turns, and — if the verdict is decisive — turn the whole document in a single save, then
    /// rebuild every surface (tiles, render cache, canvas). The pass that runs by itself stays
    /// quiet when the engine cannot judge; <paramref name="reportFailure"/> (an explicit click)
    /// says why the page was left alone and that the file is untouched.
    /// </summary>
    internal async Task AutoOrientAsync(bool reportFailure = false)
    {
        if (IsCancelled || _ocrEngine is null || IsOrienting) return;
        if (PageCount <= 0 || _isLoading) return; // let the initial paint finish first

        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "自动摆正", Path.GetFileName(_file.Path), slowMs: 0);

        IsOrienting = true;
        StatusText = "正在检测页面方向…";
        try
        {
            AutoOrientResult result = await _postProcessor
                .AutoOrientAsync(PageCount, OrientationDpi)
                .ConfigureAwait(true);
            if (IsCancelled)
            {
                op.Complete("已取消");
                return;
            }

            if (!result.OcrAvailable)
            {
                op.Complete("OCR 不可用，未改动");
                if (reportFailure)
                    ShowTransientStatus("自动摆正失败：本机没有可用的 OCR 引擎（文件未改动）");
                return;
            }

            int steps = result.Steps;
            op.Step("方向投票", steps == 0 ? "已是正确朝向" : $"需顺时针 {steps * 90}°");

            if (!result.Applied)
            {
                ShowTransientStatus("已是正向，无需调整");
                op.Complete("已是正确朝向，未改动");
                return;
            }

            op.Step("旋转写盘", $"顺时针 {steps * 90}°，共 {PageCount} 页");

            // Every /Rotate changed but the content streams did not — only the rotation metadata
            // is stale in the caches, yet page bytes differ per (page, dpi) key, so drop it all.
            BuildThumbnails();
            StartThumbnailLoad();
            op.Step("重建缩略图", $"共 {PageCount} 页");
            await RenderAsync(PageIndex, force: true);
            op.Step("画布刷新", $"第 {PageIndex + 1} 页");

            // RenderAsync clears StatusText on success; write our line after that, and leave a
            // render failure's own message standing.
            if (StatusText.Length == 0)
                ShowTransientStatus($"已自动摆正（顺时针 {steps * 90}°），已保存");
            op.Complete($"顺时针 {steps * 90}°，共 {PageCount} 页");
        }
        catch (OperationCanceledException)
        {
            op.Complete("已取消");
        }
        catch (Exception ex)
        {
            // The file is untouched (RotateAll swaps only after a complete save), but staying
            // silent made a failed click look like a dead button — say it and that nothing broke.
            op.Fail("自动摆正失败", ex);
            if (!IsCancelled) StatusText = $"自动摆正失败：{ex.Message}（文件未改动）";
        }
        finally
        {
            IsOrienting = false;
        }
    }
}
